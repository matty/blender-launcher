using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace BlenderLauncher.App.Views.Lists;

/// <summary>
/// A <see cref="ListBox"/> whose recycled rows keep their controls (smooth scrolling). Rows are built
/// from <see cref="RowTemplate"/> once per container and then only re-bound: the container's <c>DataContext</c> is the
/// item, and the row view inherits it. Set <see cref="RowTemplate"/>, not <c>ItemTemplate</c>.
/// </summary>
/// <remarks>
/// With a normal <c>ItemTemplate</c>, Avalonia 12.1 rebuilt the whole row template every time a recycled container
/// was given a new item (its <c>ContentPresenter</c> did not reuse the old child): measured at about 0.5 MB allocated
/// and 2–6 ms of layout per row scrolled into view, with a gen-1 collection every few steps.
/// </remarks>
public class RecyclingListBox : ListBox
{
    public static readonly StyledProperty<IDataTemplate?> RowTemplateProperty =
        AvaloniaProperty.Register<RecyclingListBox, IDataTemplate?>(nameof(RowTemplate));

    protected override Type StyleKeyOverride => typeof(ListBox);

    /// <summary>The row view, built once per container from the first item it shows.</summary>
    [Avalonia.Metadata.InheritDataTypeFromItems(nameof(ItemsSource))]
    public IDataTemplate? RowTemplate
    {
        get => GetValue(RowTemplateProperty);
        set => SetValue(RowTemplateProperty, value);
    }

    protected override void PrepareContainerForItemOverride(Control container, object? item, int index)
    {
        if (container is ListBoxItem row && !ReferenceEquals(container, item))
        {
            row.DataContext = item;
            if (row.Content is null && RowTemplate is { } template)
            {
                row.Content = template.Build(item);
            }
        }

        base.PrepareContainerForItemOverride(container, item, index);
    }

    protected override void ClearContainerForItemOverride(Control container)
    {
        // Keep the row view (and the old item as its data until the container is reused); only the selection goes.
        if (container is ListBoxItem row)
        {
            row.ClearValue(ListBoxItem.IsSelectedProperty);
            return;
        }

        base.ClearContainerForItemOverride(container);
    }
}
