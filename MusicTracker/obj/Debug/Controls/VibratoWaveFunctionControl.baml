   M S B A M L   `   `   `  ����G  CMusicTracker, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null9!clr-namespace:MusicTracker.EditorMusicTracker.Editor  5      =#clr-namespace:MusicTracker.ControlsMusicTracker.Controls  5      6    0MusicTracker.Controls.VibratoWaveFunctionControl   5      (ed!clr-namespace:MusicTracker.Editor  5      W SPresentationCore, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35R NWindowsBase, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35\ XPresentationFramework, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35D 9http://schemas.microsoft.com/winfx/2006/xaml/presentation    5      R NSystem.Xaml, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089:x,http://schemas.microsoft.com/winfx/2006/xaml     5      Jmc;http://schemas.openxmlformats.org/markup-compatibility/2006     5      @d2http://schemas.microsoft.com/expression/blend/2008     5      5local#clr-namespace:MusicTracker.Controls     5      .��5
      � -   5        $��gridRoot��5
      $��1��6   .+�5   
   �� -   5        $��
endControl��5      
���   ����#222222$��3q�6M     �� CornerRadius$  5��6a   $
��Left=�6r   $	��Top=�6�   $��0q�6�   ._�5      �  � ColumnDefinitions 5      �� 5      $��20 �6'   �� 5      $��* �6'   �� 5      $��20 �6'    � RowDefinitions 5      �� 5      $
��auto �6$   �� 5      $
��auto �6$   �� 5      $
��auto �6$   �� 5      $
��auto �6$   .+�5      �� $��0��6   $��3��6&   $  5��6:   ��5      �� 5       �� 
StartPoint$ 0.5,0&�6.    �� EndPoint$ 0.5,1&�6A   .��5      �  � Offset$ 0Q�6+    � Color$ #555555��66   � 5      $ 1Q�6+   $ #222222��66   5      ._�5!      �  5"      �� 5#      $��30 �6/   �� 5$      $��* �6/   .+�5&      �� -   5        $��btnRemoveNode��5&   a   $��0��6!   $	����61   $��Segoe UI Symbol)�6D   $��15(�6v   $��Center=�6�   
������ $��0q�6�   $��0q�6�   $��0q�6�   �� 5'      -   5        $��1��5'   "   ._�5(      �� $��15(�6(   $
��Bold"�66   $��Vibrato��6I   
�������$��	10,5,10,5q�6k   $��3��6~   5)      5*      5+      #   MusicTracker.Controls.NodeEnd  5/      -   5          Name$ nodePrev��5/       $��1��62   $��0��6?   $
��Left=�6O   $��Center=�6j   $��20��6�   $��20��6�   �� 50      $��=> Input��6   $��1��6,   
�������$��10q�6M   $��2��6Y   $��1��6m   � 52      $��2��6   $��1��6%    53      �� 54      $
��auto �6+   �� 55      $��* �6+    57      �� 58      $
��auto �6(   �� 59      $
��auto �6(   .+�5;      �� $��Freq. :��6    $��5q�6/   
�������$��Center=�6M   �� 5<      -   5         ��Name$ txtFreq��5<      $��1��6b   $	��100��6r   $��5q�6~   ��6/   �� 	 �� Path$	 VibratoFrequency�
 �� Mode$
 TwoWay=�6~   �� 5=      $��Speed :��6    $��5q�6/   
�������$��Center=�6M   $��1��6h   �� 5>      -   5        $ txtSpeed��5>      $��1��6_   $��1��6o   $	��100��6|   $��5q�6�   ��60   �� $	 VibratoSpeed�$
 TwoWay=�6�   5?      � 5A      $��3��6   $��3��6$    5B      �� 5C      $��* �6+   �� 5D      $
��auto �6+   .+�5F      �� $��	Output =>��6    
�������$��5q�6F   $��Right=�6R   %   MusicTracker.Controls.NodeStart  5G      -   5          Name$ nodeNext��5G   &   $��1��68   $��2��6H   $��Center=�6U   $��Center=�6r   $��20��6�   $��20��6�   $��5q�6�   5I      5J      5L      5M      5N      