   M S B A M L   `   `   `  ����  PresentationFramework.AerogJclr-namespace:Microsoft.Windows.Themes;assembly=PresentationFramework.AeroMicrosoft.Windows.Themes  5   9   W SPresentationCore, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35R NWindowsBase, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35\ XPresentationFramework, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35R NSystem.Xaml, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089  �� MergedDictionaries �� Source �� 
TargetType �� Property �� Value �� 
TargetType �� CornerRadius   BorderMediumColor �� Color  ControlLightColor  ExpandCollapseToggleStyle'   !System.Windows.VisualStateManager    VisualStateGroups%  System.Windows.VisualStateGroup	   States   System.Windows.VisualState
   
Storyboard �� TargetProperty �� 
TargetName R� KeyTime  System.Windows.Visibility   Hidden R� Value   Visible F�Name  TreeViewItemFocusVisual �� StrokeDashArray J� Opacity �� Path �� RelativeSource �� AncestorType � ColumnDefinitions � RowDefinitions8  2System.Windows.Media.Animation.EasingColorKeyFrame �� KeyTime  SelectedBackgroundColor �� Value  SelectedUnfocusedColor d�Name �� 	ClickMode d� 	IsChecked ��Name �� Padding  ��Name! ��Name" �� Triggers# P� Property$ P� Value% �� 
TargetName& ]� 
Conditions' �� Property( �� Value�� 5      D 9http://schemas.microsoft.com/winfx/2006/xaml/presentation    :x,http://schemas.microsoft.com/winfx/2006/xaml     5      gMicrosoft_Windows_ThemesJclr-namespace:Microsoft.Windows.Themes;assembly=PresentationFramework.Aero  69     5      �� 5      $ Colors.xaml?�6   %�  'R�       &	 +    &	 #    'Q� �    7  7  7  7  �� 5      " R�6   .�5   
   ��  �@�OverridesDefaultStyle . 62   �� 5   
    �@J�SnapsToDevicePixels . 60   �� 5   
    � ��$
 Auto=�6G   �� 5   
    � ��$
 Auto=�6E   �� 5   
    � �� 5      �� 5      " R�6"   .R�5      �� $��Border��6   $ 1��6+   $��1q�6<   ��5      �� 5      # �   6.   ��5      �� 5      # �  6.   ._�5      �� |�.  ��.  $��4q�6R   $��Stretch=�6^   .��5      �� $��Stretch=�6-   5      5      5      5!      5"      �� 5$      " d�5%      .�5&   
   ��  � |� .  6&   �� 5'   
    � �� 5(      �� 5)      " d�6"   .R�5*      � $��15��6   $��13��6&   
������  5+        5,      CheckStates��.	 5-   "     Checked��.
 5.   &   �� .��5/   *   T� $ (UIElement.Visibility)�6H   $ 	Collapsed��6{   ..�50   .   i� $ 0��6E   # Z 6Q   51   +   T� 52   *   $ (UIElement.Visibility)�6H   $ Expanded��53   2   ..�54   .   i� $ 0��6E   # Z 6Q   55   +   56   '   57   #     58   "   	Unchecked��6.     59   "   Indeterminate��6.   5:      .+�5<      F� $ 	Collapsed��6   $
��Left=�62   $��Center=�6M   $��1,1,1,1q�6h   "���� 	=     � z  	= � 	=  z 	��5=      �� 5>   "   $	 red��62   5@      F� 5A      $ Expanded��6   $
��Left=�61   $��Center=�6L   $��1,1,1,1q�6g   "����     	= � z  	= � 	=  z 	$y�Hidden=�6�   ��5B      �� 5C   "   $	 red��62   5E      5F      5G      5I      5J      �� 5K      .�5L   
   ��  � �� 5M      �� 5N      .R�5O      �� ._�5P      �� $��0,0,0,0q�6$   $��5��65   
���   �$	 1 2R�6Y   $ 0Q�6o   5Q      5R      5T      5U      �� 5V      " Q�6   .�5W   
   ��  � ��# � ��6'   �� 5X   
    � �� 67   �� $  HorizontalContentAlignment� �� " ���� 5Y   
    � �� 65   �� $ VerticalContentAlignment� �� " ���� 5Z   
    � ��$ 1,0,0,0q�6$   �� 5[   
    � ��# � ��6'   �� 5\   
    �@�FocusVisualStyle8   6.   �� 5]   
    � �� 5^      �� 5_      " Q�6"   .R�5`      � $��Stretch=�6    5a      �� 5b      $	��100��6/   $
��Auto �6?   �� 5c      $
��Auto �6/   �� 5d      $��* �6/    5f      �� 5g      $
��Auto �6,   �� 5h       5j        5k      SelectionStates��.	 5l   "     Selected��.
 5m   &   �� .��5n   *   �� $ Bd��6G   $1 +(Panel.Background). (SolidColorBrush.Color)�6c   .g�5o   .     $ 0��6B   8  6N   5p   +   5q   '   5r   #     5s   "   
Unselected��6.     5t   "   SelectedInactive��.
 5u   &   �� .��5v   *   �� $ Bd��6G   $0 *(Panel.Background).(SolidColorBrush.Color)�6b   .g�5w   .     $ 0��6B   8  6N   5x   +   5y   '   5z   #   5{        5|      ExpansionStates��.	 5}   "     Expanded��.
 5~   &   �� .��5   *   T� $ (UIElement.Visibility)�6H   $ 	ItemsHost��6{   ..�5�   .   i� $ 0��6E   # Z 6Q   5�   +   5�   '   5�   #     5�   "   	Collapsed��6.   5�      .+�5�      d� $ Expander��6>   $y�	Collapsed=�6'   8�� $ Press=�6�    6�   �� *
IsExpanded+ �� *TemplatedParent+�� 5�      $ Bd��6?   $��Stretch=�6!   $��1��6K   $��2��6[   #��z��6l   #��z��6�   #��z��6�   # z��6�   ._�5�      �� $  PART_Header��6/   $��Header��6D   $��Stretch=�6[   5�      �� 5�      $! 	ItemsHost��6)   $��1��6<   $��1��6I   $��2��6Y   $y�	Collapsed=�6m   $��Stretch=�6�   5�      " 5�      P� 5�      # �@Q�
IsSelected$ . . �5�      ��  � ��
 �  ��6;   5�      P� 5�      # �@��HasItems$ .  . �5�      �� $% Expander��6%    � y�$ Hidden=�6Q   5�      ]� 5�      & 5�      �� 5�   "   ' � ��( .  6A   �� 5�   "   ' � ��$
( Auto��6=   ./�5�      �� $% PART_Header��6%    � ��$ 75��6R   5�      ]� 5�      & 5�      �� 5�   "   ' � ��( .  6A   �� 5�   "   ' � ��$
( Auto��6>   ./�5�      �� $% PART_Header��6%    � ��$ 19��6S   5�      5�      5�      5�      5�      