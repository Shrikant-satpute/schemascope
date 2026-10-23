using SchemaScope.Core.Engines;
using SchemaScope.Scripting;
using SchemaScope.Scripting.PostgreSql;

namespace SchemaScope.Api.Services;

/// <summary>
/// The script generator of each engine pair this build has: SQL Server to SQL Server is the untouched one, through
/// <see cref="ScriptGeneratorFactory"/>; PostgreSQL to PostgreSQL is <see cref="PgScriptGenerator"/>, only while that engine
/// is turned on and offers scripts. Anything else has none, and the route answers 400.
/// </summary>
public sealed class EngineScriptGeneratorFactory(EngineCatalog catalog) : IScriptGeneratorFactory
{
    private readonly ScriptGeneratorFactory sqlServer = new();

    public IScriptGenerator? For(DatabaseEngine source, DatabaseEngine target) =>
        sqlServer.For(source, target) ??
        (source == DatabaseEngine.PostgreSql && target == DatabaseEngine.PostgreSql &&
         catalog.Enabled.Supports(DatabaseEngine.PostgreSql, EngineFeature.Scripts)
            ? new PgScriptGenerator()
            : null);
}
