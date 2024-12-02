﻿#pragma checksum "..\..\..\Controls\SineWaveFunctionControl.xaml" "{8829d00f-11b8-4213-878b-770e8597ac16}" "C0035C48AE7F3F0AB17D83E939669B06B39158DEC9EB35771A772A2FF7FF9A95"
//------------------------------------------------------------------------------
// <auto-generated>
//     This code was generated by a tool.
//     Runtime Version:4.0.30319.42000
//
//     Changes to this file may cause incorrect behavior and will be lost if
//     the code is regenerated.
// </auto-generated>
//------------------------------------------------------------------------------

using MusicTracker.Controls;
using MusicTracker.Editor;
using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Media.TextFormatting;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Windows.Shell;


namespace MusicTracker.Controls {
    
    
    /// <summary>
    /// SineWaveFunctionControl
    /// </summary>
    public partial class SineWaveFunctionControl : MusicTracker.Editor.BaseWaveFunctionControl, System.Windows.Markup.IComponentConnector {
        
        
        #line 10 "..\..\..\Controls\SineWaveFunctionControl.xaml"
        [System.Diagnostics.CodeAnalysis.SuppressMessageAttribute("Microsoft.Performance", "CA1823:AvoidUnusedPrivateFields")]
        internal System.Windows.Controls.Grid gridRoot;
        
        #line default
        #line hidden
        
        
        #line 11 "..\..\..\Controls\SineWaveFunctionControl.xaml"
        [System.Diagnostics.CodeAnalysis.SuppressMessageAttribute("Microsoft.Performance", "CA1823:AvoidUnusedPrivateFields")]
        internal System.Windows.Controls.Border endControl;
        
        #line default
        #line hidden
        
        
        #line 36 "..\..\..\Controls\SineWaveFunctionControl.xaml"
        [System.Diagnostics.CodeAnalysis.SuppressMessageAttribute("Microsoft.Performance", "CA1823:AvoidUnusedPrivateFields")]
        internal System.Windows.Controls.Button btnRemoveNode;
        
        #line default
        #line hidden
        
        
        #line 43 "..\..\..\Controls\SineWaveFunctionControl.xaml"
        [System.Diagnostics.CodeAnalysis.SuppressMessageAttribute("Microsoft.Performance", "CA1823:AvoidUnusedPrivateFields")]
        internal System.Windows.Controls.Grid contentPath;
        
        #line default
        #line hidden
        
        
        #line 51 "..\..\..\Controls\SineWaveFunctionControl.xaml"
        [System.Diagnostics.CodeAnalysis.SuppressMessageAttribute("Microsoft.Performance", "CA1823:AvoidUnusedPrivateFields")]
        internal MusicTracker.Controls.NodeStart nodeNext;
        
        #line default
        #line hidden
        
        private bool _contentLoaded;
        
        /// <summary>
        /// InitializeComponent
        /// </summary>
        [System.Diagnostics.DebuggerNonUserCodeAttribute()]
        [System.CodeDom.Compiler.GeneratedCodeAttribute("PresentationBuildTasks", "4.0.0.0")]
        public void InitializeComponent() {
            if (_contentLoaded) {
                return;
            }
            _contentLoaded = true;
            System.Uri resourceLocater = new System.Uri("/MusicTracker;component/controls/sinewavefunctioncontrol.xaml", System.UriKind.Relative);
            
            #line 1 "..\..\..\Controls\SineWaveFunctionControl.xaml"
            System.Windows.Application.LoadComponent(this, resourceLocater);
            
            #line default
            #line hidden
        }
        
        [System.Diagnostics.DebuggerNonUserCodeAttribute()]
        [System.CodeDom.Compiler.GeneratedCodeAttribute("PresentationBuildTasks", "4.0.0.0")]
        [System.Diagnostics.CodeAnalysis.SuppressMessageAttribute("Microsoft.Performance", "CA1811:AvoidUncalledPrivateCode")]
        internal System.Delegate _CreateDelegate(System.Type delegateType, string handler) {
            return System.Delegate.CreateDelegate(delegateType, this, handler);
        }
        
        [System.Diagnostics.DebuggerNonUserCodeAttribute()]
        [System.CodeDom.Compiler.GeneratedCodeAttribute("PresentationBuildTasks", "4.0.0.0")]
        [System.ComponentModel.EditorBrowsableAttribute(System.ComponentModel.EditorBrowsableState.Never)]
        [System.Diagnostics.CodeAnalysis.SuppressMessageAttribute("Microsoft.Design", "CA1033:InterfaceMethodsShouldBeCallableByChildTypes")]
        [System.Diagnostics.CodeAnalysis.SuppressMessageAttribute("Microsoft.Maintainability", "CA1502:AvoidExcessiveComplexity")]
        [System.Diagnostics.CodeAnalysis.SuppressMessageAttribute("Microsoft.Performance", "CA1800:DoNotCastUnnecessarily")]
        void System.Windows.Markup.IComponentConnector.Connect(int connectionId, object target) {
            switch (connectionId)
            {
            case 1:
            this.gridRoot = ((System.Windows.Controls.Grid)(target));
            return;
            case 2:
            this.endControl = ((System.Windows.Controls.Border)(target));
            return;
            case 3:
            this.btnRemoveNode = ((System.Windows.Controls.Button)(target));
            
            #line 36 "..\..\..\Controls\SineWaveFunctionControl.xaml"
            this.btnRemoveNode.Click += new System.Windows.RoutedEventHandler(this.btnRemoveNode_Click);
            
            #line default
            #line hidden
            return;
            case 4:
            
            #line 37 "..\..\..\Controls\SineWaveFunctionControl.xaml"
            ((System.Windows.Controls.Border)(target)).MouseDown += new System.Windows.Input.MouseButtonEventHandler(this.Control_MouseDown);
            
            #line default
            #line hidden
            
            #line 37 "..\..\..\Controls\SineWaveFunctionControl.xaml"
            ((System.Windows.Controls.Border)(target)).MouseMove += new System.Windows.Input.MouseEventHandler(this.Control_MouseMove);
            
            #line default
            #line hidden
            
            #line 37 "..\..\..\Controls\SineWaveFunctionControl.xaml"
            ((System.Windows.Controls.Border)(target)).MouseUp += new System.Windows.Input.MouseButtonEventHandler(this.Control_MouseUp);
            
            #line default
            #line hidden
            return;
            case 5:
            this.contentPath = ((System.Windows.Controls.Grid)(target));
            return;
            case 6:
            this.nodeNext = ((MusicTracker.Controls.NodeStart)(target));
            return;
            }
            this._contentLoaded = true;
        }
    }
}

