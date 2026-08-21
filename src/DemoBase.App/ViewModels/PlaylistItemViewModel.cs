using CommunityToolkit.Mvvm.ComponentModel;
using DemoBase.Core.Models;
using System.Collections.ObjectModel;

namespace DemoBase.App.ViewModels;

/// <summary>Wrapper d'affichage pour une Playlist — nom, sélection et pistes
/// chargées (résolues via JOIN sur FavoriteSoundtracks côté service).
/// IsSelected fait double emploi : déplier la liste de ses pistes ET la
/// désigner comme cible active du bouton "➕" sur les favoris non classés
/// (une seule playlist sélectionnée à la fois, gérée par
/// FavoriteSoundtracksViewModel.SelectPlaylist).</summary>
public partial class PlaylistItemViewModel : ObservableObject
{
    public int Id { get; }

    [ObservableProperty] private string _name;
    [ObservableProperty] private bool   _isSelected;
    [ObservableProperty] private ObservableCollection<FavoriteSoundtrack> _tracks = [];

    public int TrackCount => Tracks.Count;

    public PlaylistItemViewModel(Playlist playlist)
    {
        Id   = playlist.Id;
        _name = playlist.Name;
        _tracks.CollectionChanged += (_, _) => OnPropertyChanged(nameof(TrackCount));
    }

    // 2026-08-19 : le générateur CommunityToolkit.Mvvm déclare le paramètre "oldValue"
    // des méthodes partielles On<Prop>Changing/On<Prop>Changed comme nullable (T?),
    // même quand le champ source ([ObservableProperty] private ObservableCollection<...>
    // _tracks = [];) est lui-même non-nullable — la valeur initiale du champ (avant tout
    // affectation) n'est pas garantie par le générateur. Sans ce "?" ici, la signature ne
    // correspond pas à la déclaration générée → CS8611. Null-check ajouté en conséquence.
    partial void OnTracksChanged(
        ObservableCollection<FavoriteSoundtrack>? oldValue,
        ObservableCollection<FavoriteSoundtrack> newValue)
    {
        if (oldValue != null)
            oldValue.CollectionChanged -= OnTracksCollectionChanged;
        newValue.CollectionChanged += OnTracksCollectionChanged;
        OnPropertyChanged(nameof(TrackCount));
    }

    private void OnTracksCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => OnPropertyChanged(nameof(TrackCount));
}
