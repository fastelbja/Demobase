using DemoBase.Core.Interfaces;
using DemoBase.Core.Models;
using DemoBase.Data.Context;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using System.IO;

namespace DemoBase.Media;

// ─── Emulator Service (stub pour DemoBase.Media) ──────────────────────────────

public class EmulatorService : IEmulatorService
{
    public Task<bool> TestExecutableAsync(int emulatorId)
        => Task.FromResult(false);

    public async Task LaunchReleaseAsync(ReleaseLink link, EmulatorConfig config)
    {
        if (link.LocalFilePath == null)
            throw new InvalidOperationException("Pas de fichier local.");
        var args = await BuildCommandLineAsync(config, link.LocalFilePath);
        Process.Start(new ProcessStartInfo
        {
            FileName        = config.Emulator.ExecutablePath,
            Arguments       = args,
            UseShellExecute = false,
        });
    }

    public Task LaunchReleaseAsync(string romPath, Release release, EmulatorConfig config)
    {
        var args = config.CommandLine
            .Replace("{file}", $"\"{romPath}\"")
            .Replace("{configFile}", config.ConfigFilePath != null
                ? $"\"{config.ConfigFilePath}\"" : "");
        Process.Start(new ProcessStartInfo
        {
            FileName        = config.Emulator.ExecutablePath,
            Arguments       = args.Trim(),
            UseShellExecute = false,
        });
        return Task.CompletedTask;
    }

    public Task<string> BuildCommandLineAsync(EmulatorConfig config, string filePath)
    {
        var args = config.CommandLine
            .Replace("{file}",       $"\"{filePath}\"")
            .Replace("{configFile}", config.ConfigFilePath != null
                ? $"\"{config.ConfigFilePath}\"" : "");
        return Task.FromResult(args.Trim());
    }
}

// ─── Media Service ────────────────────────────────────────────────────────────

public class MediaService : IMediaService
{
    private readonly IDbContextFactory<DemoBaseDbContext> _ctxFactory;
    private readonly string _mediaRoot;

    private static readonly HashSet<string> TrackerExtensions =
        [".mod", ".s3m", ".it", ".xm", ".ft2", ".669", ".mtm", ".stm"];

    public MediaService(IDbContextFactory<DemoBaseDbContext> ctxFactory)
    {
        _ctxFactory = ctxFactory;
        _mediaRoot  = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DemoBase", "Media");
        Directory.CreateDirectory(_mediaRoot);
    }

    public async Task<string> AddScreenshotAsync(int releaseId, string sourcePath)
        => await AddMediaAsync(releaseId, sourcePath, Core.Enums.MediaType.Screenshot, "screenshots");

    public async Task<string> AddVideoAsync(int releaseId, string sourcePath)
        => await AddMediaAsync(releaseId, sourcePath, Core.Enums.MediaType.Video, "videos");

    public async Task<string> AddMusicAsync(int releaseId, string sourcePath)
    {
        var ext  = Path.GetExtension(sourcePath).ToLowerInvariant();
        var type = TrackerExtensions.Contains(ext)
            ? Core.Enums.MediaType.ModMusic
            : Core.Enums.MediaType.AudioMusic;
        return await AddMediaAsync(releaseId, sourcePath, type, "music");
    }

    private async Task<string> AddMediaAsync(int releaseId, string sourcePath,
        Core.Enums.MediaType type, string sub)
    {
        var dir  = Path.Combine(_mediaRoot, releaseId.ToString(), sub);
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, Path.GetFileName(sourcePath));
        File.Copy(sourcePath, dest, overwrite: true);

        await using var ctx = await _ctxFactory.CreateDbContextAsync();
        ctx.MediaFiles.Add(new MediaFile
        {
            ReleaseId = releaseId,
            Type      = type,
            FilePath  = Path.GetRelativePath(_mediaRoot, dest),
            Format    = Path.GetExtension(sourcePath).TrimStart('.').ToUpper(),
        });
        await ctx.SaveChangesAsync();
        return dest;
    }

    public async Task DeleteMediaAsync(int mediaFileId)
    {
        await using var ctx = await _ctxFactory.CreateDbContextAsync();
        var media = await ctx.MediaFiles.FindAsync(mediaFileId);
        if (media == null) return;
        var full = Path.Combine(_mediaRoot, media.FilePath);
        if (File.Exists(full)) File.Delete(full);
        ctx.MediaFiles.Remove(media);
        await ctx.SaveChangesAsync();
    }

    public Task<byte[]?> GetThumbnailAsync(int mediaFileId)
        => Task.FromResult<byte[]?>(null);
}
