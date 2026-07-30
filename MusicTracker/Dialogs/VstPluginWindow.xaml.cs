using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using MusicTracker.Engine.Timeline.Effects;

namespace MusicTracker.Dialogs
{
    /// <summary>
    /// Fenêtre WPF non-modale qui héberge la GUI native (HWND Win32) d'un plugin VST via <see cref="HwndHost"/>.
    /// La taille de la fenêtre est ajustée à ce que le plugin déclare via <c>EditorGetRect</c>. Un timer 30 Hz
    /// appelle <see cref="IVstEditorHost.EditorIdle"/> — obligatoire pour beaucoup de plugins qui redessinent leurs
    /// sliders/VU en Idle plutôt que par WM_PAINT.
    ///
    /// L'hôte est passé sous forme d'interface (<see cref="IVstEditorHost"/>) : la fenêtre est agnostique entre
    /// un <see cref="VstEffect"/> (insert audio) et un <see cref="MusicTracker.Engine.Timeline.VstInstrument"/>
    /// (VSTi source sonore). À la fermeture, on ferme proprement l'éditeur côté plugin (<c>EditorClose</c>) sans
    /// disposer l'hôte lui-même — l'audio doit continuer même si l'utilisateur ferme la GUI.
    /// </summary>
    public partial class VstPluginWindow : Window
    {
        readonly IVstEditorHost _host;
        VstHwndHost _hwnd;
        DispatcherTimer _idleTimer;

        public VstPluginWindow(IVstEditorHost host, Window owner)
        {
            InitializeComponent();
            _host = host ?? throw new ArgumentNullException(nameof(host));
            Owner = owner;
            titleText.Text = host.DisplayName;

            Loaded += OnLoaded;
        }

        void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Force le chargement synchrone SI pas encore chargé (l'audio ne l'a peut-être jamais appelé si la
            // lecture n'est pas en cours). 512 = block-size par défaut, sera renégocié quand le vrai renderer prend la main.
            if (!_host.EnsureOpenedSync(512))
            {
                titleText.Text = _host.DisplayName + " — " + Localization.Loc.T("VstPluginFailedToLoad");
                return;
            }
            var sz = _host.GetEditorSize();
            if (sz.Width > 100 && sz.Height > 50)
            {
                // 6 = bordures, 46 = hauteur en-tête + padding — ne pas être trop serré.
                Width = sz.Width + 6;
                Height = sz.Height + 46;
            }

            _hwnd = new VstHwndHost(_host);
            hostBorder.Child = _hwnd;

            _idleTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(33) };
            _idleTimer.Tick += (a, b) => _host.EditorIdle();
            _idleTimer.Start();
        }

        void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_idleTimer != null) { _idleTimer.Stop(); _idleTimer = null; }
            if (_hwnd != null) { hostBorder.Child = null; _hwnd.Dispose(); _hwnd = null; }
            _host.CloseEditor();
        }

        void Close_Click(object sender, RoutedEventArgs e) => Close();
    }

    /// <summary>
    /// Adaptateur <see cref="HwndHost"/> qui crée un HWND enfant Win32 vide (une "static" quasi-transparente)
    /// et demande au plugin d'ouvrir son éditeur DANS ce HWND. Le plugin se re-parente comme il l'entend
    /// (certains créent un HWND child, d'autres l'éditent en place).
    /// </summary>
    internal class VstHwndHost : HwndHost
    {
        const uint WS_CHILD = 0x40000000;
        const uint WS_VISIBLE = 0x10000000;

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName,
            uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll")]
        static extern bool DestroyWindow(IntPtr hWnd);

        readonly IVstEditorHost _host;
        IntPtr _child;

        public VstHwndHost(IVstEditorHost host) { _host = host; }

        protected override HandleRef BuildWindowCore(HandleRef hwndParent)
        {
            // HWND "STATIC" (contrôle Win32 built-in) — conteneur passif que le plugin peut repeupler.
            // Style WS_CHILD+WS_VISIBLE, taille 1×1 initiale (redimensionnée automatiquement par HwndHost via WM_SIZE).
            _child = CreateWindowEx(0, "STATIC", "", WS_CHILD | WS_VISIBLE, 0, 0, 1, 1, hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            _host.OpenEditor(_child);
            return new HandleRef(this, _child);
        }

        protected override void DestroyWindowCore(HandleRef hwnd)
        {
            _host.CloseEditor();
            if (hwnd.Handle != IntPtr.Zero) DestroyWindow(hwnd.Handle);
            _child = IntPtr.Zero;
        }
    }
}
