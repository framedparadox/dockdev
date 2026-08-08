using System.Runtime.CompilerServices;
using dockdev.Services;

namespace dockdev.Tests;

/// <summary>
/// Settles the string table once, before any test runs.
/// <para>
/// Without this, <see cref="Loc"/> is uninitialized for the whole suite and every lookup returns
/// the bare key — which is fine until a test wants to assert something about a message a user would
/// actually read. Doing it from a test's constructor instead would be worse than doing nothing:
/// xUnit runs test classes in parallel, so it would mutate process-wide state <em>while</em>
/// another class is midway through comparing two strings that both came from <see cref="Loc"/>
/// (<c>ToolDockItemTests</c> does exactly that), and the failure would be an occasional one nobody
/// could reproduce.
/// </para>
/// <para>
/// A module initializer runs before any type in this assembly is touched, so there is no window in
/// which some tests see keys and others see sentences. English specifically, not the agent's
/// display language, so an assertion about a message means the same thing on every machine.
/// </para>
/// </summary>
internal static class TestSetup
{
    [ModuleInitializer]
    internal static void Initialize() => Loc.Initialize("en");
}
