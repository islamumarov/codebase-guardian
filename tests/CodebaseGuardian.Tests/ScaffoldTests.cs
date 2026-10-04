namespace CodebaseGuardian.Tests;

public class ScaffoldTests
{
    [Fact]
    public void Server_assembly_is_loadable()
    {
        var assembly = typeof(Program).Assembly;

        Assert.Equal("CodebaseGuardian", assembly.GetName().Name);
    }
}
