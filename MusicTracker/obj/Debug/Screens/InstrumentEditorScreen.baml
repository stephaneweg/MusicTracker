   M S B A M L   `   `   `  ����G  CMusicTracker, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null;"clr-namespace:MusicTracker.ScreensMusicTracker.Screens  5      =#clr-namespace:MusicTracker.ControlsMusicTracker.Controls  5      1    +MusicTracker.Screens.InstrumentEditorScreen   5   	   W SPresentationCore, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35R NWindowsBase, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35\ XPresentationFramework, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35D 9http://schemas.microsoft.com/winfx/2006/xaml/presentation    5      R NSystem.Xaml, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089:x,http://schemas.microsoft.com/winfx/2006/xaml     5      Jmc;http://schemas.openxmlformats.org/markup-compatibility/2006     5      @d2http://schemas.microsoft.com/expression/blend/2008     5      4local"clr-namespace:MusicTracker.Screens     5      8controls#clr-namespace:MusicTracker.Controls     5      -   5           CommonBackground#��[  5	   8   .��5      �   � RowDefinitions  5   
   �� 5      $
��auto �6   �� 5      $
��auto �6   �� 5      $��* �6   �� 5      $��5 �6   �� 5      $	��100 �6   .+�5   
   o� $��1��6   $��0,0,0,0q�6   .@�5      m� -   5        $��
menuExport��5      $��Export instrument��6)   6D   m� 5      -   5        $��headerPressets��5   +   $��
Presets...��6   ��5      �� 5      .R�5      �� ��6$   ��  �� Path$
 Name�5      5      m� 5      $��Add component...��6   .@�5      m� -   5        $ ��addWaveFunction_AudioPatch��5      $��Audio patch��6=   6R   m� 5      -   5        $��addWaveFunction_Sinus��5      $��
Sinus Wave��68   6L   m� 5      -   5        $��addWaveFunction_Square��5      $��Square Wave��69   6N   m� 5       -   5        $��addWaveFunction_Triangle��5       $��Triangle Wave��6;   6R   m� 5!      -   5        $��addWaveFunction_SawTooth��5!      $��SawTooth Wave��6;   6R   m� 5"      -	   5        $��addWaveFunction_Noise��5"      $��Noise��68   6G   m� 5#      -
   5        $��addWaveFunction_Vibrato��5#      $��Vibrato��6:   6K   m� 5$      -   5        $'��!addWaveFunction_FrequencyModifier��5$      $��Frequency Modifier��6D   6`   m� 5%      -   5        $(��"addWaveFunction_FrequencyModulator��5%      $��Frequency Modulator��6E   6b   m� 5&      -   5        $#��addWaveFunction_EnveloppeADSR��5&      $��ADSR Enveloppe��6@   6X   m� 5'      -   5        $��addWaveFunction_Add��5'      $��Wave Addition��66   6M   m� 5(      -   5        $��addWaveFunction_Mul��5(      $��Wave Multiplication��66   6S   5)      m� 5+      -   5        $��menuPlay��5+      $
��Play��6'   65   5,      �� 5.   
   $��3��6   $��10��6$   
���   �$��Stretch=�6C   �� 5/   
   -   5        $��scrollDetails��5/   n   $��2��6   $
��Auto=�6&   $
��Auto=�6K   .��51      � -   5        $��gridRoot��51      $��1��6#   ��52      �� 53      -   5        brushBG�� �� ImageSource$ /gridbg.png��53   2    j� ViewportUnits$ RelativeToBoundingBox=�6M    j� TileMode$
 Tile=�6s   .+�55      �� -   5        $��
endControl��55      
���   ����#222222$��3q�6�    �� CornerRadius$ 5��6�   $
��Left=�6�   $	��Top=�6�   $��10q�6   J� Effect 56      3  -System.Windows.Media.Effects.DropShadowEffect  57         Color$ 	#88888888��6+   ._�59      � -   5        $��
ctrlOutput��59      .+�5:      �� $��Output��6$   $��20(�62   
�������$��20q�6S   #   MusicTracker.Controls.NodeEnd  5;      -   5          Name$ endNode��5;   +   $��	-5,5,5,-5q�6<   $��Center=�6O   $
��Left=�6j   $��20��6�   $��20��6�   5<      5?      5@      5B      .   (MusicTracker.Controls.WaveFunctionVisual  5D   
   -   5        	  Name$	 
waveVisual��5D   &   $��4��6:   6I   5E      5G      