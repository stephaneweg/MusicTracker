   M S B A M L   `   `   `  ����G  CMusicTracker, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null;"clr-namespace:MusicTracker.ScreensMusicTracker.Screens  5   	   +    %MusicTracker.Screens.InstrumentSelect   5   	   W SPresentationCore, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35R NWindowsBase, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35\ XPresentationFramework, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35D 9http://schemas.microsoft.com/winfx/2006/xaml/presentation    5   	   R NSystem.Xaml, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089:x,http://schemas.microsoft.com/winfx/2006/xaml     5   	   @d2http://schemas.microsoft.com/expression/blend/2008     5   	   Jmc;http://schemas.openxmlformats.org/markup-compatibility/2006     5   	   4local"clr-namespace:MusicTracker.Screens     5   	     � Title$  Select an instrument��5   	   $	��350��6&   $	��400��63      CommonBackground#��[  6?     ButtonForeground#��[ 6o   .��5	      � $��10q�6    � RowDefinitions 5
   
   �� 5      $��* �6   �� 5      $
��auto �6   �� 5      $
��auto �6   �� 5      $
��auto �6   .+�5   
   �� $��0��6   $��0q�6$   $
��Auto=�6/   .��5      �� -   5        $��listInstrument��5      $��1��6-     DarkBackground#��[ 6;     LightBackground#��[ 6h   $��5q�6�    �� SelectionMode$ Single=�6�    � 	Resources 5       �� 
TargetType �� Property �� Value��%Y   '��       �� 5      " ��6   .�5      ��  � y�$ 	Collapsed=�67   5      5      ��5      �� 5      .R�5      �  � ColumnDefinitions 5      �� 5   "   $��* �63    5      �� 5   "   $
��auto �60   .+�5!      �� 
�������$��15(�6z   ��6(   ��  �� Path$
 Name�	 �� UpdateSourceTrigger$	 PropertyChanged=�6z   5#      5$      5&      5'      � 5)   
   $��1��6    5*      �� 5+      $
��auto �6#   �� 5,      $��* �6#   .+�5.      �� -   5        $��	txtSelect��5.   +   $��New name : ��6   $��5q�6<   $��Center=�6G   �� 5/      -   5        
 ��Name$
 txtInstrumentName��5/   &   $��1��6    � ToolTip$ Enter the instrument name��6A   $��5q�6�   $��Center=�6�     labelTextBox#��[ 6�   ��6e   �� $
 Name� �� Mode$ TwoWay=�6�   50      �� 51   
   $��2��6   $��Right=�6"    �� Orientation$ 
Horizontal=�6>   .+�52      �� -   5         ��Name$ btnOK��52      $��75��6$   $��Select��6/   6@   �� 53      -   5        $ 	btnCancel��53      $��75��6(   $��Cancel��63   6D   54      55      56      