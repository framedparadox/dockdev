using dockdev.ToolPages;
using Xunit;

namespace dockdev.Tests.Tools;

/// <summary>
/// <see cref="ToolCommand.Invoke"/>'s callers are all event handlers with no caller of their own —
/// a command-bar button's <c>Click</c>, a <c>KeyboardAccelerator</c>'s <c>Invoked</c>,
/// <c>EditorToolPage</c>'s tunnelling key handler — so a throw out of a tool's action is an
/// unhandled exception on the UI thread. The actions run over whatever the user pasted, which is
/// the input a parser is most likely to be surprised by.
/// </summary>
public class ToolCommandTests
{
    private static ToolCommand Command(Action execute, Func<bool>? canExecute = null) =>
        new("label", "glyph", execute, canExecute: canExecute, id: "test");

    [Fact]
    public void Invoke_RunsTheAction()
    {
        int runs = 0;
        Command(() => runs++).Invoke();
        Assert.Equal(1, runs);
    }

    [Fact]
    public void Invoke_DoesNotRunTheActionWhenItCannotExecute()
    {
        int runs = 0;
        Command(() => runs++, canExecute: () => false).Invoke();
        Assert.Equal(0, runs);
    }

    [Fact]
    public void Invoke_SwallowsAThrowFromTheAction()
    {
        var command = Command(() => throw new InvalidOperationException("boom"));

        // No assertion beyond "this returns": the point is that a failing command leaves the pane
        // it was going to write as it was, rather than ending the process.
        command.Invoke();
    }

    [Fact]
    public void Invoke_KeepsWorkingAfterAFailure()
    {
        bool fail = true;
        int runs = 0;
        var command = Command(() =>
        {
            runs++;
            if (fail)
                throw new FormatException("boom");
        });

        command.Invoke();
        fail = false;
        command.Invoke();

        Assert.Equal(2, runs);
    }
}
