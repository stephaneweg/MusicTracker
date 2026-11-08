---
name: wpf-dialog-inprocess-harness
description: "vérifier/photographier un dialogue WPF sans lancer l'app — héberger Application en process (new MusicTracker.App + InitializeComponent) et rendre en PNG"
metadata: 
  node_type: memory
  type: reference
  originSessionId: 358b357f-06c6-4108-9fb9-cf5c4bf4da89
  modified: 2026-07-30T10:40:44.441Z
---

Pour **vérifier un dialogue** (allure stylée, contenu, câblage) sans lancer KotonStudio ni piloter FlaUI : héberger WPF **en process** dans un harnais console.

```csharp
[STAThread] static int Main() {
    var app = new MusicTracker.App();
    app.InitializeComponent();      // charge les dictionnaires de style de App.xaml SANS lancer OnStartup
    var dlg = new MusicTracker.Dialogs.XxxDialog(...);
    dlg.Loaded += (s,e) => dlg.Dispatcher.BeginInvoke(new Action(() => {
        Save(dlg, png); dlg.Close();      // rendu après layout
    }), DispatcherPriority.ApplicationIdle);
    dlg.ShowDialog();
}
```

- `InitializeComponent()` est le point clé : sans lui tous les `{StaticResource CommonBackground}`/`okButton`/`labelTextBox` lèvent. `OnStartup` ne tourne pas (pas de `app.Run()`), donc pas d'effets de bord.
- Rendu : `RenderTargetBitmap` sur **`(FrameworkElement)w.Content`** (le `Border` racine), pas sur la Window — les dialogues sont `AllowsTransparency` + `WindowStyle=None`, la fenêtre elle-même est transparente.
- Un dialogue **modal** bloque le harnais : un `DispatcherTimer` continue de tourner (la boucle imbriquée de `ShowDialog` le pompe) et peut le fermer automatiquement — c'est comme ça qu'on teste un dialogue déclenché par un gestionnaire d'exception (voir [[bug-report-github]]).

Script : `scratchpad/build_run_wpf.ps1` (copie `bin\Release` dans `wpfbin`, compile avec csc). Gotchas csc : les assemblies WPF ne se résolvent PAS par nom simple, il faut le chemin complet dans `Reference Assemblies\…\v4.8\` ; et ne PAS référencer `System.Drawing` (déjà auto-référencé → CS1703). Voir aussi `build_run_rel.ps1` pour les harnais non-UI.
