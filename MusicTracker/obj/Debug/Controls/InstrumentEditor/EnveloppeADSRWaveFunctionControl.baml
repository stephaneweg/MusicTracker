   M S B A M L   `   `   `  ����G  CMusicTracker, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null9!clr-namespace:MusicTracker.EditorMusicTracker.Editor  5      _4clr-namespace:MusicTracker.Controls.InstrumentEditor&MusicTracker.Controls.InstrumentEditor  5      =#clr-namespace:MusicTracker.ControlsMusicTracker.Controls  5      M    GMusicTracker.Controls.InstrumentEditor.EnveloppeADSRWaveFunctionControl   5      (ed!clr-namespace:MusicTracker.Editor  5      W SPresentationCore, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35R NWindowsBase, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35\ XPresentationFramework, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35D 9http://schemas.microsoft.com/winfx/2006/xaml/presentation    5      R NSystem.Xaml, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089:x,http://schemas.microsoft.com/winfx/2006/xaml     5      Jmc;http://schemas.openxmlformats.org/markup-compatibility/2006     5      @d2http://schemas.microsoft.com/expression/blend/2008     5      Flocal4clr-namespace:MusicTracker.Controls.InstrumentEditor     5      8controls#clr-namespace:MusicTracker.Controls     5      .��5      � -   5        $��gridRoot��5      $��1��6   .+�5   
   �� -   5        $��
endControl��5      
���   ����#222222$��3q�6M     �� CornerRadius$  5��6a   $
��Left=�6r   $	��Top=�6�   $��0q�6�   ._�5      �  � ColumnDefinitions 5      �� 5      $��20 �6'   �� 5      $��* �6'   �� 5      $��20 �6'    � RowDefinitions 5      �� 5      $
��auto �6$   �� 5      $
��auto �6$   �� 5      $
��auto �6$   �� 5      $
��auto �6$   .+�5      �� $��0��6   $��3��6&   $  5��6:   ��5      �� 5       �� 
StartPoint$ 0.5,0&�6.    �� EndPoint$ 0.5,1&�6A   .��5      �  � Offset$ 0Q�6+    � Color$ #555555��66   � 5      $ 1Q�6+   $ #222222��66   5       ._�5"      �  5#      �� 5$      $��30 �6/   �� 5%      $��* �6/   .+�5'      �� -   5        $��btnRemoveNode��5'   a   $��0��6!   $	����61   $��Segoe UI Symbol)�6D   $��15(�6v   $��Center=�6�   
������ $��0q�6�   $��0q�6�   $��0q�6�   �� 5(      -   5        $��1��5(   "   ._�5)      �� $��15(�6(   $
��Bold"�66   $��ADSR Envelope��6I   
�������$��	10,5,10,5q�6q   $��3��6�   5*      5+      5,      #   MusicTracker.Controls.NodeEnd  50      -   5          Name$ nodePrev��50   #   $��1��65   $��0��6B   $
��Left=�6R   $��Center=�6m   $��20��6�   $��20��6�   �� 51      $��=> Input��6   $��1��6,   
�������$��10q�6M   $��2��6Y   $��1��6m   � 53      $��2��6   $��1��6%    54      �� 55      $
��auto �6+   �� 56      $��* �6+    58      �� 59      $
��auto �6(   �� 5:      $
��auto �6(   �� 5;      $
��auto �6(   �� 5<      $
��auto �6(   .+�5>      �� $��Attack :��6    $��5q�60   
�������$��Center=�6N   �� 5?      -   5         ��Name$ 	txtAttack��5?      $��1��6Z   $	��100��6j   $��5q�6v   ��61   �� 	 �� Path$	 Attack�
 �� Mode$
 TwoWay=�6v   �� 5@      $��Decay :��6    $��1��6/   $��5q�6<   
�������$��Center=�6Z   �� 5A      -   5        $ txtDecay��5A      $��1��61   $��1��6f   $	��100��6v   $��5q�6�   ��6>   �� $	 Decay�$
 TwoWay=�6�   �� 5B      $��	Sustain :��6    $��2��61   $��5q�6>   
�������$��Center=�6\   �� 5C      -   5        $ 
txtSustain��5C      $��2��63   $��1��6j   $	��100��6z   $��5q�6�   ��6@   �� $	 Sustain�$
 TwoWay=�6�   �� 5D      $��	Release :��6    $��3��61   $��5q�6>   
�������$��Center=�6\   �� 5E      -	   5        $ 
txtRelease��5E      $��3��63   $��1��6j   $	��100��6z   $��5q�6�   ��6@   �� $	 Release�$
 TwoWay=�6�   5F      � 5H      $��3��6   $��3��6$    5I      �� 5J      $��* �6+   �� 5K      $
��auto �6+   .+�5M      �� $��	Output =>��6    
�������$��5q�6F   $��Right=�6R   %   MusicTracker.Controls.NodeStart  5N      -
   5          Name$ nodeNext��5N   )   $��1��6;   $��2��6K   $��Center=�6X   $��Center=�6u   $��20��6�   $��20��6�   $��5q�6�   5P      5Q      5S      5T      5U      