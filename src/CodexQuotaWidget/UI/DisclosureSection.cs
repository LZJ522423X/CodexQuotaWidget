using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;

namespace CodexQuotaWidget;

internal static class DisclosureSection
{
    internal static Expander Create(string heading,UIElement content,string name)
    {
        var section=new Expander{Name=name,Header=heading,Content=content,IsExpanded=false,Margin=new Thickness(0,0,0,10),Template=SectionTemplate.Value};
        section.SetResourceReference(Control.ForegroundProperty,"Ink");System.Windows.Automation.AutomationProperties.SetName(section,heading);return section;
    }
    static readonly Lazy<ControlTemplate> SectionTemplate=new(()=>(ControlTemplate)XamlReader.Parse("""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Expander">
          <Border BorderBrush="{DynamicResource Line}" Background="{DynamicResource Surface}" BorderThickness="1" CornerRadius="10">
            <StackPanel>
              <ToggleButton x:Name="HeaderButton" Foreground="{DynamicResource Ink}" HorizontalContentAlignment="Stretch" MinHeight="48"
                  IsChecked="{Binding IsExpanded, RelativeSource={RelativeSource TemplatedParent}}" Content="{TemplateBinding Header}">
                <ToggleButton.Template><ControlTemplate TargetType="ToggleButton">
                  <Border x:Name="HeaderBorder" Background="Transparent" CornerRadius="9" Padding="14,10" BorderBrush="Transparent" BorderThickness="1">
                    <Grid><Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width="24"/></Grid.ColumnDefinitions>
                      <ContentPresenter VerticalAlignment="Center"/>
                      <TextBlock x:Name="Arrow" Grid.Column="1" Text="&#xE70D;" FontFamily="Segoe Fluent Icons" FontSize="12" HorizontalAlignment="Right" VerticalAlignment="Center"/>
                    </Grid>
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="HeaderBorder" Property="Background" Value="{DynamicResource Hover}"/></Trigger>
                    <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="HeaderBorder" Property="BorderBrush" Value="{DynamicResource Accent}"/></Trigger>
                    <Trigger Property="IsChecked" Value="True"><Setter TargetName="Arrow" Property="Text" Value="&#xE70E;"/></Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate></ToggleButton.Template>
              </ToggleButton>
              <ContentPresenter x:Name="Body" Visibility="Collapsed" Margin="14,4,14,14"/>
            </StackPanel>
          </Border>
          <ControlTemplate.Triggers><Trigger Property="IsExpanded" Value="True"><Setter TargetName="Body" Property="Visibility" Value="Visible"/></Trigger></ControlTemplate.Triggers>
        </ControlTemplate>
        """));
}
