using CommunityToolkit.Mvvm.ComponentModel;

namespace BlenderLauncher.App.ViewModels;

/// <summary>
/// Base of every view model. <see cref="ObservableObject"/> from CommunityToolkit.Mvvm: use
/// <c>[ObservableProperty] public partial T Name { get; set; }</c> and <c>[RelayCommand]</c> (source-generated, AOT-safe).
/// View models are touched on the UI thread only; services raise events on worker threads, so marshal them with
/// <see cref="Services.UiDispatcher"/> first.
/// </summary>
public abstract class ViewModelBase : ObservableObject
{
}
