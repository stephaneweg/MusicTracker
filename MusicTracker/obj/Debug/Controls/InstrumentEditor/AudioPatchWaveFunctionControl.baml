   M S B A M L   `   `   `  ����G  CMusicTracker, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null9!clr-namespace:MusicTracker.EditorMusicTracker.Editor  5      _4clr-namespace:MusicTracker.Controls.InstrumentEditor&MusicTracker.Controls.InstrumentEditor  5      =#clr-namespace:MusicTracker.ControlsMusicTracker.Controls  5      J    DMusicTracker.Controls.InstrumentEditor.AudioPatchWaveFunctionControl   5      (ed!clr-namespace:MusicTracker.Editor  5      W SPresentationCore, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35R NWindowsBase, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35\ XPresentationFramework, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35D 9http://schemas.microsoft.com/winfx/2006/xaml/presentation    5      R NSystem.Xaml, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089:x,http://schemas.microsoft.com/winfx/2006/xaml     5      Jmc;http://schemas.openxmlformats.org/markup-compatibility/2006     5      @d2http://schemas.microsoft.com/expression/blend/2008     5      Flocal4clr-namespace:MusicTracker.Controls.InstrumentEditor     5      8controls#clr-namespace:MusicTracker.Controls     5      1   +MusicTracker.Editor.BaseWaveFunctionControl    Loaded$$  BaseWaveFunctionControl_Loaded��5	      .��5      � -   5        $��gridRoot��5      $��1��6   .+�5   
   �� -   5        $��
endControl��5      
���   ����#222222$��3q�6L    �� CornerRadius$ 5��6`   $
��Left=�6q   $	��Top=�6�   $��0q�6�   ._�5      �  � ColumnDefinitions 5      �� 5      $��20 �6'   �� 5      $��* �6'   �� 5      $��20 �6'    � RowDefinitions 5      �� 5      $
��auto �6$   �� 5      $
��auto �6$   �� 5      $��* �6$   �� 5      $
��auto �6$   .+�5      �� $��0��6   $��3��6&   $ 5��6:   ��5      �� 5       �� 
StartPoint$ 0.5,0&�6.    �� EndPoint$ 0.5,1&�6A   .��5      �  � Offset$ 0Q�6+    � Color$ #555555��66   � 5      $ 1Q�6+   $ #222222��66   5       ._�5"      �  5#      �� 5$      $��30 �6/   �� 5%      $��* �6/   .+�5'      �� -   5        $��btnRemoveNode��5'   a   $��0��6!   $	����61   $��Segoe UI Symbol)�6D   $��15(�6v   $��Center=�6�   
������ $��0q�6�   $��0q�6�   $��0q�6�   �� 5(      -   5        $��1��5(   "   ._�5)      �� $��15(�6(   $
��Bold"�66   $��Audio Patch��6I   
�������$��	10,5,10,5q�6o   $��3��6�   5*      5+      5,      � 5-      $��1��6   $��1��6$    5.      �� 5/      $
��auto �6(   �� 50      $
��auto �6(   .+�52      �� $��Patch :��6    
�������$��5q�6B   $
��Left=�6M   �� 53      -   5        $��cbPatch��53      $��1��6.    �� DisplayMemberPath$
 Name��6p   	 �� SelectedValuePath$
	 Name��6�   
 �� SelectedValue
 6;   ��  �� Path$ 	PatchName� �� Mode$ TwoWay=�6�   54      � 55      -   5        $��contentPath��55      $��2��6*   $��3��67   $	��100��6K   $��75��6W   $��Center=�6c   � 57      $��3��6   $��3��6$    58      �� 59      $��* �6+   �� 5:      $
��auto �6+   .+�5<      �� $��	Output =>��6    
�������$��5q�6F   $��Right=�6R   %   MusicTracker.Controls.NodeStart  5=      -   5          Name$ nodeNext��5=   )   $��1��6;   $��2��6K   $��Center=�6X   $��Center=�6u   $��20��6�   $��20��6�   $��5q�6�   5?      5@      5A      5B      5C      