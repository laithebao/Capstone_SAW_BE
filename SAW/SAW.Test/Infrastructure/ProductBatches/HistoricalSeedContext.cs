using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using SAW.Infrastructure.Persistence;

namespace SAW.Test.Infrastructure.ProductBatches;

internal static class HistoricalSeedContext
{
    // Seed the schema under test without current application notification hooks.
    internal static DbContext Create(AppDbContext reference, Migration migration)
    {
        // Snapshot entities use dictionary property bags. Seed CLR graphs with the
        // application's mappings restricted to the historical schema instead.
        var builder = new ModelBuilder(reference.GetService<IConventionSetBuilder>().CreateConventionSet());
        builder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
        foreach (var entity in builder.Model.GetEntityTypes().ToArray())
            if (migration.TargetModel.FindEntityType(entity.Name) is null) builder.Ignore(entity.ClrType);
        foreach (var entity in builder.Model.GetEntityTypes().ToArray())
        {
            var old = migration.TargetModel.FindEntityType(entity.Name)!;
            foreach (var navigation in entity.GetNavigations().ToArray())
                if (old.FindNavigation(navigation.Name) is null) builder.Entity(entity.ClrType).Ignore(navigation.Name);
            foreach (var property in entity.GetProperties().ToArray())
                if (old.FindProperty(property.Name) is null) builder.Entity(entity.ClrType).Ignore(property.Name);
        }
        var model = reference.GetService<IModelRuntimeInitializer>().Initialize(builder.FinalizeModel(), designTime: false);
        return new DbContext(new DbContextOptionsBuilder().UseSqlServer(reference.Database.GetConnectionString())
            .UseModel(model).Options);
    }

    internal static async Task<DbContext> ForAppliedSchemaAsync(AppDbContext reference)
    {
        var last = (await reference.Database.GetAppliedMigrationsAsync()).Last();
        var assembly = reference.GetService<IMigrationsAssembly>();
        return Create(reference, assembly.CreateMigration(assembly.Migrations[last], reference.Database.ProviderName!));
    }
}
