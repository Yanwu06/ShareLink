using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
namespace ShareLink;
public sealed class ShareModeSwitch : UserControl
{
    public static readonly DependencyProperty IsSingleProperty=DependencyProperty.Register(nameof(IsSingle),typeof(bool),typeof(ShareModeSwitch),new PropertyMetadata(false,(o,_)=>((ShareModeSwitch)o).UpdateSelection(true)));
    public bool IsSingle{get=>(bool)GetValue(IsSingleProperty);set=>SetValue(IsSingleProperty,value);}
    public event Action<bool>? SelectionRequested;
    private readonly TranslateTransform slide=new(150,0);
    private readonly System.Windows.Shapes.Path single,dual;
    private readonly TextBlock singleLabel,dualLabel;
    public ShareModeSwitch()
    {
        Width=300;Height=46;
        var root=new Grid{ClipToBounds=true};root.Children.Add(new Border{Background=Brushes.White,BorderBrush=new SolidColorBrush(Color.FromRgb(219,225,234)),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(8)});
        root.Children.Add(new Border{Width=146,Height=40,Background=new SolidColorBrush(Color.FromRgb(36,99,235)),CornerRadius=new CornerRadius(8),Margin=new Thickness(2,0,0,0),HorizontalAlignment=HorizontalAlignment.Left,RenderTransform=slide});
        var buttons=new Grid();buttons.ColumnDefinitions.Add(new());buttons.ColumnDefinitions.Add(new());
        void Make(string title,string glyph,int column,bool mode,out System.Windows.Shapes.Path icon,out TextBlock label){
            icon=new(){Data=(Geometry)FindResource(glyph),Width=23,Height=23,Stretch=Stretch.Uniform};label=new(){Text=title,FontSize=16,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(8,0,0,0)};
            var row=new StackPanel{Orientation=Orientation.Horizontal};row.Children.Add(icon);row.Children.Add(label);
            var b=new Button{Content=row,ToolTip=title,Style=(Style)FindResource("ModeSegment")};System.Windows.Automation.AutomationProperties.SetName(b,title);b.Click+=(_,_)=>SelectionRequested?.Invoke(mode);Grid.SetColumn(b,column);buttons.Children.Add(b);
        }
        Make("浏览器分享","SingleModeGlyph",0,true,out single,out singleLabel);Make("设备互传","DualModeGlyph",1,false,out dual,out dualLabel);root.Children.Add(buttons);Content=root;
        UpdateSelection(false);Loaded+=(_,_)=>UpdateSelection(false);IsEnabledChanged+=(_,_)=>Opacity=IsEnabled?1:.65;
    }
    private void UpdateSelection(bool animate){if(single is null)return;double x=IsSingle?0:150;if(animate&&IsLoaded&&SystemParameters.ClientAreaAnimation)slide.BeginAnimation(TranslateTransform.XProperty,new DoubleAnimation(x,TimeSpan.FromMilliseconds(180)){EasingFunction=new QuadraticEase{EasingMode=EasingMode.EaseOut}});else{slide.BeginAnimation(TranslateTransform.XProperty,null);slide.X=x;}
        var dark=new SolidColorBrush(Color.FromRgb(38,51,67));single.Fill=singleLabel.Foreground=IsSingle?Brushes.White:dark;dual.Fill=dualLabel.Foreground=IsSingle?dark:Brushes.White;
    }
}
