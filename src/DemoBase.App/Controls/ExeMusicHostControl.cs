using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DemoBase.App.Controls;

/// <summary>
/// HwndHost qui intègre la fenêtre d'un exe externe (executable music)
/// directement dans l'interface WPF de DemoBase.
/// </summary>
public class ExeMusicHostControl : HwndHost
{
    private nint _childHwnd = nint.Zero;

    [DllImport("user32.dll")] private static extern nint SetParent(nint hWndChild, nint hWndNewParent);
    [DllImport("user32.dll")] private static extern int  SetWindowLong(nint hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] private static extern int  GetWindowLong(nint hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    private const int GWL_STYLE   = -16;
    private const int WS_CHILD    = 0x40000000;
    private const int WS_CAPTION  = 0x00C00000;
    private const int WS_THICKFRAME = 0x00040000;
    private const int WS_MAXIMIZE = 0x01000000;
    private const uint SWP_NOZORDER   = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const int SW_SHOW = 5;

    public ExeMusicHostControl(nint childHwnd)
    {
        _childHwnd = childHwnd;
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        if (_childHwnd == nint.Zero)
            return new HandleRef(this, nint.Zero);

        // Retirer les décorations de fenêtre (bordure, barre titre)
        var style = GetWindowLong(_childHwnd, GWL_STYLE);
        style &= ~WS_CAPTION;
        style &= ~WS_THICKFRAME;
        style |= WS_CHILD;
        SetWindowLong(_childHwnd, GWL_STYLE, style);

        // Reparenter dans notre HwndHost
        SetParent(_childHwnd, hwndParent.Handle);
        ShowWindow(_childHwnd, SW_SHOW);

        // Redimensionner pour remplir le conteneur
        var size = GetContainerSize();
        SetWindowPos(_childHwnd, nint.Zero, 0, 0, (int)size.Width, (int)size.Height,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);

        return new HandleRef(this, _childHwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        // Ne pas détruire la fenêtre — elle appartient au process exe
        // Le process sera tué par ExeMusicPlayer.Stop() si nécessaire
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (_childHwnd != nint.Zero)
            SetWindowPos(_childHwnd, nint.Zero, 0, 0,
                (int)sizeInfo.NewSize.Width, (int)sizeInfo.NewSize.Height,
                SWP_NOZORDER | SWP_NOACTIVATE);
    }

    private Size GetContainerSize()
    {
        return new Size(ActualWidth > 0 ? ActualWidth : 800,
                        ActualHeight > 0 ? ActualHeight : 600);
    }

    public void Detach()
    {
        if (_childHwnd != nint.Zero)
        {
            // Remettre la fenêtre en mode standalone avant de la détruire
            var style = GetWindowLong(_childHwnd, GWL_STYLE);
            style &= ~WS_CHILD;
            style |= WS_CAPTION;
            SetWindowLong(_childHwnd, GWL_STYLE, style);
            SetParent(_childHwnd, nint.Zero);
            _childHwnd = nint.Zero;
        }
    }
}
