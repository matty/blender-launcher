using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Metadata;

namespace BlenderLauncher.App.Views.Lists;

/// <summary>
/// Content for a rare row state (download progress, "Installed", an unrecognised or damaged folder), built from
/// <see cref="Template"/> only the first time <see cref="IsActive"/> is true and hidden otherwise. Rows are recycled
/// as the list scrolls, so states most rows never show cost nothing until a row needs them.
/// </summary>
public sealed class LazyContent : Decorator
{
    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<LazyContent, bool>(nameof(IsActive));

    public static readonly StyledProperty<IDataTemplate?> TemplateProperty =
        AvaloniaProperty.Register<LazyContent, IDataTemplate?>(nameof(Template));

    public LazyContent()
    {
        IsVisible = false;
    }

    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    /// <summary>The state's controls (a <c>DataTemplate</c> with the row's <c>x:DataType</c>).</summary>
    [Content]
    public IDataTemplate? Template
    {
        get => GetValue(TemplateProperty);
        set => SetValue(TemplateProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsActiveProperty)
        {
            var active = change.GetNewValue<bool>();
            if (active && Child is null && Template is { } template)
            {
                Child = template.Build(DataContext);
            }

            IsVisible = active;
        }
    }
}
