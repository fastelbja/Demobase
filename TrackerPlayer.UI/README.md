# TrackerPlayer.UI — Intégration dans un projet WPF

## Référencer la DLL

```xml
<!-- Dans votre .csproj -->
<ProjectReference Include="..\TrackerPlayer.UI\TrackerPlayer.UI.csproj" />
<!-- ou depuis NuGet (si publié) -->
<PackageReference Include="TrackerPlayer.UI" Version="1.0.0" />
```

## Charger les ressources (App.xaml)

```xml
<Application.Resources>
    <ResourceDictionary>
        <ResourceDictionary.MergedDictionaries>
            <ResourceDictionary
                Source="pack://application:,,,/TrackerPlayer.UI;component/Assets/TrackerPlayerResources.xaml"/>
        </ResourceDictionary.MergedDictionaries>
    </ResourceDictionary>
</Application.Resources>
```

## Utiliser PatternView en XAML

```xml
<Window
    xmlns:ctrl="clr-namespace:TrackerPlayer.UI.Controls;assembly=TrackerPlayer.UI"
    xmlns:conv="clr-namespace:TrackerPlayer.UI.Converters;assembly=TrackerPlayer.UI">

    <Window.Resources>
        <conv:BoolToVisibilityConverter x:Key="BoolVis"/>
    </Window.Resources>

    <ctrl:PatternView
        CurrentVm="{Binding CurrentPatternVm}"
        HighlightedRow="{Binding HighlightedRow}"
        TrackerStyle="{Binding CurrentTrackerStyle}"/>
</Window>
```

## Propriétés de PatternView

| Propriété | Type | Description |
|-----------|------|-------------|
| `CurrentVm` | `PatternViewModel?` | Pattern à afficher (pré-calculé) |
| `HighlightedRow` | `int` | Ligne surlignée (ligne de lecture courante) |
| `TrackerStyle` | `TrackerStyle` | Style visuel : `ProTracker`, `FastTracker2`, `ScreamTracker3`, `ImpulseTracker` |

## Préparer les données (ViewModel)

```csharp
using TrackerPlayer.UI.Controls;

// Pré-calcul du cache (une seule fois au chargement, sur thread pool)
var cache = await Task.Run(() =>
{
    var dict = new Dictionary<int, PatternViewModel>(module.Patterns.Count);
    foreach (var p in module.Patterns)
        dict[p.Index] = new PatternViewModel(p);
    return dict;
});

// Changement de pattern = O(1)
cache.TryGetValue(patternIndex, out var vm);
CurrentPatternVm = vm;

// Mise à jour de la ligne (depuis StateChanged de ITrackerPlayer)
HighlightedRow = state.CurrentRow;

// Style automatique selon le format
CurrentTrackerStyle = module.Format switch
{
    TrackerFormat.XM  => TrackerStyle.FastTracker2,
    TrackerFormat.S3M => TrackerStyle.ScreamTracker3,
    TrackerFormat.IT  => TrackerStyle.ImpulseTracker,
    TrackerFormat.MOD => module.Channels <= 4
                         ? TrackerStyle.ProTracker
                         : TrackerStyle.FastTracker2,
    _                 => TrackerStyle.ProTracker
};
```

## Contenu de la DLL

```
TrackerPlayer.UI/
├── Controls/
│   └── PatternView.xaml(.cs)    ← UserControl rendu DrawingContext
│       ├── PatternViewModel      ← données immuables pré-calculées
│       ├── DrawingVisualHost     ← wrapper UIElement pour DrawingVisual
│       └── TrackerStyle (enum)   ← ProTracker / FT2 / S3M / IT
├── Converters/
│   ├── BoolToVisibilityConverter
│   ├── SecondsToTimeConverter
│   └── StringToVisibilityConverter
└── Assets/
    ├── Styles.xaml               ← thème sombre TrackerPlayer
    └── TrackerPlayerResources.xaml ← point d'entrée ressources
```
