using System.Reflection;
using System.Threading;
using System.Windows;
using UserControl = System.Windows.Controls.UserControl;
using Xunit;

namespace EveDeck.Tests;

// Proves that splitting MainWindow.xaml's TabItem content out into Views/Tabs/*.xaml UserControls
// (see MainWindow.xaml.cs's history) did not leave any StaticResource lookup dangling. A separately
// compiled UserControl parses its own BAML in its own constructor, before it joins any window's
// visual tree, so a XAML reference to a resource that only exists in a Window.Resources block would
// throw XamlParseException here at construction time -- the compiler cannot catch that. Every
// resource these tabs need lives in App.Resources (Theme.xaml / SeatCardTemplate.xaml, merged in
// App.xaml, plus the two converters App.RegisterSharedResources assigns in code), matching exactly
// what App.xaml.cs's Main does before creating MainWindow.
public class TabUserControlSmokeTests
{
    [Fact]
    public void AllTabUserControls_ConstructWithoutException()
    {
        Exception? failure = null;
        List<Type>? constructedTypes = null;

        var thread = new Thread(() =>
        {
            try
            {
                var app = System.Windows.Application.Current as EveDeck.App;
                if (app is null)
                {
                    app = new EveDeck.App();
                    app.InitializeComponent();
                }
                EveDeck.App.RegisterSharedResources(app);

                var tabTypes = typeof(EveDeck.App).Assembly.GetTypes()
                    .Where(t => t is { IsClass: true, IsAbstract: false, Namespace: "EveDeck.Views.Tabs" }
                                && typeof(UserControl).IsAssignableFrom(t))
                    .OrderBy(t => t.Name)
                    .ToList();

                foreach (var type in tabTypes)
                {
                    var instance = Activator.CreateInstance(type);
                    if (instance is null)
                        throw new InvalidOperationException($"{type.Name} constructed to null.");
                }

                constructedTypes = tabTypes;
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
            throw new InvalidOperationException(
                $"Constructing a Views.Tabs.*Tab UserControl threw: {failure}", failure);

        Assert.NotNull(constructedTypes);
        // Guards the test itself against silently matching zero types if the namespace or base
        // class ever changes out from under it.
        Assert.Equal(10, constructedTypes!.Count);
    }
}
