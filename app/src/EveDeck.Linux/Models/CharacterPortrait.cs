using Avalonia.Media;
using EveDeck.Utilities;

namespace EveDeck.Models;

// Linux counterpart of the Windows app's CharacterPortrait: same shape, Avalonia image type. Shared
// models reference this type by name, so it has to exist on both sides.
public sealed class CharacterPortrait : ObservableObject
{
    private IImage? _image;

    public CharacterPortrait(long characterId) => CharacterId = characterId;

    public long CharacterId { get; }

    public IImage? Image
    {
        get => _image;
        set
        {
            if (ReferenceEquals(_image, value)) return;
            _image = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasImage));
        }
    }

    public bool HasImage => _image is not null;
}
