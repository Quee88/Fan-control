using CommunityToolkit.Mvvm.ComponentModel;
using FanControl.Models;

namespace FanControl.ViewModels;

/// <summary>Profil v rozbalovacím seznamu – obal kvůli oznámení o přejmenování.</summary>
public sealed partial class ProfileItemViewModel(FanProfile model) : ObservableObject
{
    public FanProfile Model { get; } = model;

    public string Name
    {
        get => Model.Name;
        set
        {
            if (Model.Name == value) return;
            Model.Name = value;
            OnPropertyChanged();
        }
    }

    public override string ToString() => Name;
}
