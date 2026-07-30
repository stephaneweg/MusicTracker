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
            // Le titre est celui de la fenêtre Windows standard (chrome native, plus de fausse barre de titre).
            Title = host.DisplayName;

            Loaded += OnLoaded;
        }

        void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Force le chargement synchrone SI pas encore chargé (l'audio ne l'a peut-être jamais appelé si la
            // lecture n'est pas en cours). 512 = block-size par défaut, sera renégocié quand le vrai renderer prend la main.
            if (!_host.EnsureOpenedSync(512))
            {
                Title = _host.DisplayName + " — " + Localization.Loc.T("VstPluginFailedToLoad");
                return;
            }
            var sz = _host.GetEditorSize();
            if (sz.Width > 100 && sz.Height > 50)
            {
                // On fixe la taille du HOST BORDER à la taille exacte demandée par le plugin, et WPF
                // dimensionne la Window automatiquement (SizeToContent=WidthAndHeight) pour l'englober
                // en ajoutant juste ce qu'il faut de chrome native (barre titre + bordures Windows).
                // Résultat : la fenêtre colle pile poil à la GUI du plugin, plus de zone noire.
                hostBorder.Width = sz.Width;
                hostBorder.Height = sz.Height;
            }

            _hwnd = new VstHwndHost(_host);
            hostBorder.Child = _hwnd;

            // Ecouter les demandes de resize du plugin (via IPlugFrame.resizeView). Plein de plugins VST3
            // reportent une taille par defaut petite via getSize(), puis demandent leur vraie taille apres
            // attached() via ce callback. Sans le brancher, la fenetre reste trop petite et la GUI est
            // tronquee. Se marshalle sur le thread UI parce que le plugin peut appeler depuis n'importe ou.
            _host.EditorResizeRequested += OnPluginResizeRequested;

            _idleTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(33) };
            _idleTimer.Tick += (a, b) => _host.EditorIdle();
            _idleTimer.Start();

            // Re-query getSize APRES un cycle du dispatcher : plein de plugins VST3 font une init asynchrone
            // au createView / attached et retournent une taille par defaut au 1er appel de getSize (avant
            // que leur GUI ne soit vraiment calculee). Attendre un tour de boucle permet aux WM_ messages
            // et au layout WPF d'arriver au plugin, qui rapporte alors sa vraie taille. Idempotent : si la
            // taille n'a pas change (plugin qui reponds correctement des le 1er coup), no-op.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    var sz2 = _host.GetEditorSize();
                    if (sz2.Width > 100 && sz2.Height > 50 &&
                        (Math.Abs(sz2.Width - hostBorder.ActualWidth) > 4 || Math.Abs(sz2.Height - hostBorder.ActualHeight) > 4))
                    {
                        hostBorder.Width = sz2.Width;
                        hostBorder.Height = sz2.Height;
                        this.Width = sz2.Width;
                        this.Height = sz2.Height;
                    }
                }
                catch { }
            }), DispatcherPriority.Loaded);
        }

        void OnPluginResizeRequested(int width, int height)
        {
            if (width < 50 || height < 50) return;
            Dispatcher.BeginInvoke((Action)(() =>
            {
                // Comme dans OnLoaded : SizeToContent=WidthAndHeight ne recalcule pas toujours quand seul
                // le contenu change, on set explicitement Window.Width/Height en plus du hostBorder.
                hostBorder.Width = width;
                hostBorder.Height = height;
                this.Width = width;
                this.Height = height;
            }));
        }

        void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // Ordre STRICT :
            //  1) Arrêter le timer Idle : plus d'appels au plugin après ce point.
            //  2) Détacher le HwndHost → WPF déclenche DestroyWindowCore qui appelle _host.CloseEditor() UNE FOIS
            //     puis DestroyWindow sur le HWND enfant. On NE double-appelle PAS CloseEditor ici — plein de
            //     plugins natifs crashent sur un second EditorClose (release d'un HWND déjà libéré).
            if (_idleTimer != null) { _idleTimer.Stop(); _idleTimer = null; }
            try { if (_host != null) _host.EditorResizeRequested -= OnPluginResizeRequested; } catch { }
            if (_hwnd != null) { hostBorder.Child = null; _hwnd.Dispose(); _hwnd = null; }
        }

    }

    /// <summary>
    /// Adaptateur <see cref="HwndHost"/> qui crée un HWND enfant Win32 (fenêtre custom simple, background noir,
    /// WndProc = DefWindowProc) et demande au plugin d'ouvrir son éditeur DANS ce HWND. Historiquement on
    /// utilisait "STATIC" (contrôle Win32 built-in), mais son comportement de paint mange le WM_ERASEBKGND de
    /// certains plugins → GUI reste noire. Une classe custom règle ça (c'est le pattern utilisé par tous les DAW).
    /// </summary>
    internal class VstHwndHost : HwndHost
    {
        const uint WS_CHILD = 0x40000000;
        const uint WS_VISIBLE = 0x10000000;
        const uint WS_CLIPCHILDREN = 0x02000000;  // ne redessine pas les zones des children (le plugin gère sa propre peinture)
        const int  IDC_ARROW = 32512;
        const int  COLOR_BLACK = 0x00000000;

        // Classe Win32 enregistrée une seule fois (statique). Le nom doit être unique par process.
        static readonly string _className = "KotonStudioVstHost_" + System.Diagnostics.Process.GetCurrentProcess().Id;
        static readonly WndProc _wndProcDelegate = DefWindowProcW;  // ancre GC pour éviter la collecte du delegate
        static bool _classRegistered;
        static readonly object _classLock = new object();

        delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WNDCLASSEX
        {
            public uint cbSize;
            public uint style;
            [MarshalAs(UnmanagedType.FunctionPtr)] public WndProc lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
            public IntPtr hIconSm;
        }

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr CreateWindowExW(uint dwExStyle, string lpClassName, string lpWindowName,
            uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern ushort RegisterClassExW(ref WNDCLASSEX lpwcx);
        [DllImport("user32.dll")]
        static extern bool DestroyWindow(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        static extern IntPtr LoadCursorW(IntPtr hInstance, int cursor);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr GetModuleHandleW(string lpModuleName);
        [DllImport("gdi32.dll")]
        static extern IntPtr CreateSolidBrush(int color);

        static void EnsureClassRegistered()
        {
            lock (_classLock)
            {
                if (_classRegistered) return;
                var wc = new WNDCLASSEX
                {
                    cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                    style = 0,
                    lpfnWndProc = _wndProcDelegate,
                    hInstance = GetModuleHandleW(null),
                    hCursor = LoadCursorW(IntPtr.Zero, IDC_ARROW),
                    hbrBackground = CreateSolidBrush(COLOR_BLACK), // noir mat — la GUI du plugin recouvrira
                    lpszClassName = _className,
                };
                RegisterClassExW(ref wc);
                _classRegistered = true;
            }
        }

        readonly IVstEditorHost _host;
        IntPtr _child;
        bool _closed;    // garde d'idempotence : évite un double EditorClose (plein de plugins crashent dessus).

        public VstHwndHost(IVstEditorHost host) { _host = host; }

        protected override HandleRef BuildWindowCore(HandleRef hwndParent)
        {
            EnsureClassRegistered();
            var sz = _host.GetEditorSize();
            int w = sz.Width > 0 ? sz.Width : 400;
            int h = sz.Height > 0 ? sz.Height : 300;
            // Custom class + WS_CLIPCHILDREN : les child windows du plugin gèrent leur propre paint,
            // notre parent ne repeint jamais par-dessus. Fond noir mat le temps que le plugin peigne.
            _child = CreateWindowExW(0, _className, "", WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN, 0, 0, w, h, hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            _host.OpenEditor(_child);
            return new HandleRef(this, _child);
        }

        protected override void DestroyWindowCore(HandleRef hwnd)
        {
            // Idempotent : Dispose peut être appelé plusieurs fois selon le chemin de fermeture.
            if (!_closed) { _closed = true; try { _host.CloseEditor(); } catch { } }
            if (hwnd.Handle != IntPtr.Zero) { try { DestroyWindow(hwnd.Handle); } catch { } }
            _child = IntPtr.Zero;
        }
    }
}
