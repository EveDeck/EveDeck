using System.Windows.Media;
using EveDeck.Utilities;

namespace EveDeck.Models;

// One ship type's cached icon as an observable ImageSource, same shape as CharacterPortrait.
// ShipIconCacheService hands out a single shared instance per type id, so every row referencing the
// same hull updates together once the icon lands. A hull's icon never changes, so unlike a portrait
// there is no TTL/staleness check -- once cached, it is cached forever.
public sealed class ShipIcon : ObservableObject
{
    private ImageSource? _image;

    public ShipIcon(int typeId) => TypeId = typeId;

    public int TypeId { get; }

    public ImageSource? Image
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
