   M S B A M L   `   `   `  ����G  CMusicTracker, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null9!clr-namespace:MusicTracker.EditorMusicTracker.Editor  5      _4clr-namespace:MusicTracker.Controls.InstrumentEditor&MusicTracker.Controls.InstrumentEditor  5      =#clr-namespace:MusicTracker.ControlsMusicTracker.Controls  5      C    =MusicTracker.Controls.InstrumentEditor.AddWaveFunctionControl   5      (ed!clr-namespace:MusicTracker.Editor  5      W SPresentationCore, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35R NWindowsBase, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35\ XPresentationFramework, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35D 9http://schemas.microsoft.com/winfx/2006/xaml/presentation    5      R NSystem.Xaml, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089:x,http://schemas.microsoft.com/winfx/2006/xaml     5      Jmc;http://schemas.openxmlformats.org/markup-compatibility/2006     5      @d2http://schemas.microsoft.com/expression/blend/2008     5      Flocal4clr-namespace:MusicTracker.Controls.InstrumentEditor     5      8controls#clr-namespace:MusicTracker.Controls     5      .��5      � -   5        $��gridRoot��5      $��1��6   .+�5   
   �� -   5        $��
endControl��5      
���   ����#222222$��3q�6M     �� CornerRadius$  5��6a   $
��Left=�6r   $	��Top=�6�   $��0q�6�   ._�5      �  � ColumnDefinitions 5      �� 5      $��20 �6'   �� 5      $
��auto �6'   �� 5      $��* �6'   �� 5      $��20 �6'    � RowDefinitions 5      �� 5      $
��auto �6$   �� 5      $
��auto �6$   �� 5      $
��auto �6$   �� 5      $
��auto �6$   �� 5      $
��auto �6$   .+�5      �� $��0��6   $��4��6&   $  5��6:   ��5      �� 5       �� 
StartPoint$ 0.5,0&�6.    �� EndPoint$ 0.5,1&�6A   .��5       �  � Offset$ 0Q�6+    � Color$ #555555��66   � 5!      $ 1Q�6+   $ #222222��66   5"      ._�5$      �  5%      �� 5&      $��30 �6/   �� 5'      $��* �6/   .+�5)      �� -   5        $��btnRemoveNode��5)   a   $��0��6!   $	����61   $��Segoe UI Symbol)�6D   $��15(�6v   $��Center=�6�   
������ $��0q�6�   $��0q�6�   $��0q�6�   �� 5*      -   5        $��1��5*   "   ._�5+      �� $��15(�6(   $
��Bold"�66   $��Wave addition��6I   
�������$��	10,5,10,5q�6q   $��3��6�   5,      5-      5.      �� 51      $��Volume��6   $��1��6*   $��2��67   
�������6G   #   MusicTracker.Controls.NodeEnd  52      -   5          Name$ nodePrevWave1��52   #   $��2��6:   $��0��6G   $
��Left=�6W   $��Center=�6r   $��20��6�   $��20��6�   �� 53      $��	=> Wave 1��6   $��2��6-   
�������$��10q�6N   $��1��6Z   $��1��6n   �� 54       � Minimum$ 0Q�6   	 � Maximum$	 1Q�6%   $��2��61   $��2��6>   $��Center=�6y   $��75��6�   
 � Value
 6N   ��  �� Path$ Volume1� �� Mode$ TwoWay=�6�     57      -   5        $ nodePrevWave2��57   #   $��3��6:   $��0��6G   $
��Left=�6W   $��Center=�6r   $��20��6�   $��20��6�   �� 58      $��	=> Wave 2��6   $��3��6-   
�������$��10q�6N   $��1��6Z   $��1��6n   �� 59      $ 0Q�6   $	 1Q�6%   $��3��61   $��2��6>   $��Center=�6y   $��75��6�   
 6N   �� $ Volume2�$ TwoWay=�6�   � 5=      $��4��6   $��4��6$    5>      �� 5?      $��* �6+   �� 5@      $
��auto �6+   .+�5B      �� $��	Output =>��6    
�������$��5q�6F   $��Right=�6R   %   MusicTracker.Controls.NodeStart  5C      -   5          Name$ nodeNext��5C   )   $��1��6;   $��2��6K   $��Center=�6X   $��Center=�6u   $��20��6�   $��20��6�   $��5q�6�   5E      5F      5H      5I      5J      