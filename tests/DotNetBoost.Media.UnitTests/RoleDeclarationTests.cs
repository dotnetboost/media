using DotNetBoost.Media.Core;
using DotNetBoost.Media.Core.Attributes;
using DotNetBoost.Media.Core.Metadata;
using DotNetBoost.Media.Core.Models;
using DotNetBoost.Media.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetBoost.Media.UnitTests;

/// <summary>
/// Reading <c>[MediaRole]</c> off a type, and turning it into a registry.
/// </summary>
public class RoleDiscoveryTests
{
    [MediaEntity("DiscoveryProduct")]
    [MediaRole("primary")]
    [MediaRole("gallery", Multiple = true)]
    [MediaRole("seo")]
    private sealed class Product
    {
        public int Id { get; set; }
    }

    // Abstract so an assembly scan skips it. It exists to prove Of() refuses a contradiction, and a
    // scan that tripped over it would say nothing about scanning.
    [MediaRole("Primary")]
    [MediaRole("PRIMARY")]
    private abstract class Contradictory
    {
        public int Id { get; set; }
    }

    private sealed class Undeclared
    {
        public int Id { get; set; }
    }

    [Fact]
    public void Roles_are_read_in_the_order_they_are_written()
    {
        var roles = MediaEntityRoles.Of(typeof(Product));

        Assert.Equal(
            [("primary", false), ("gallery", true), ("seo", false)],
            roles.Select(r => (r.Name, r.Multiple)));
    }

    [Fact]
    public void A_type_that_declares_nothing_has_no_roles() =>
        Assert.Empty(MediaEntityRoles.Of(typeof(Undeclared)));

    [Fact]
    public void Declaring_the_same_role_twice_is_refused()
    {
        // Lower-cased, so these are one role written two ways rather than two roles.
        var error = Assert.Throws<InvalidOperationException>(() => MediaEntityRoles.Of(typeof(Contradictory)));

        Assert.Contains("more than once", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_registry_answers_by_the_entitys_stored_name()
    {
        var registry = Registry.Of<Product>();

        // "DiscoveryProduct", from [MediaEntity], not the class name.
        Assert.True(registry.Declares("DiscoveryProduct"));
        Assert.False(registry.Declares("RoleDiscoveryTests+Product"));

        Assert.True(registry.Find("DiscoveryProduct", "gallery")!.Value.Multiple);
        Assert.False(registry.Find("DiscoveryProduct", "primary")!.Value.Multiple);
        Assert.Null(registry.Find("DiscoveryProduct", "galery"));
    }

    [Fact]
    public void A_registry_is_case_insensitive_about_the_role_asked_for() =>
        Assert.NotNull(Registry.Of<Product>().Find("DiscoveryProduct", "GALLERY"));

    [Fact]
    public void An_undeclared_entity_declares_nothing()
    {
        var registry = Registry.Of<Product>();

        Assert.False(registry.Declares("Invoice"));
        Assert.Empty(registry.RolesFor("Invoice"));
    }
}

/// <summary>
/// The rules the engine enforces once an entity has declared its roles — and the fact that it
/// enforces nothing when an entity has not.
/// </summary>
public class RoleEnforcementTests
{
    [MediaEntity("Product")]
    [MediaRole("primary")]
    [MediaRole("gallery", Multiple = true)]
    private sealed class Product
    {
        public int Id { get; set; }
    }

    /// <summary>An entity with no attributes at all, sharing the engine with the declared one.</summary>
    private sealed class Ticket
    {
        public int Id { get; set; }
    }

    private static Harness Declared() => Harness.Create(roles: Registry.Of<Product>());

    [Fact]
    public async Task A_role_the_entity_never_declared_is_refused()
    {
        var harness = Declared();
        var file    = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png()));

        var error = await Assert.ThrowsAsync<InvalidMediaException>(
            () => harness.Media.For(new Product { Id = 1 }).AddAsync(file.Id, "galery"));

        // The message names what IS declared, because the caller has just misspelled one of them.
        Assert.Contains("no media role 'galery'", error.Message, StringComparison.Ordinal);
        Assert.Contains("primary", error.Message, StringComparison.Ordinal);
        Assert.Contains("gallery (many)", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Appending_a_second_file_to_a_single_valued_role_is_refused()
    {
        var harness = Declared();
        var product = new Product { Id = 1 };

        var first  = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png(), "a.png"));
        var second = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png(32), "b.png"));

        await harness.Media.For(product).AddAsync(first.Id, "primary");

        var error = await Assert.ThrowsAsync<InvalidMediaException>(
            () => harness.Media.For(product).AddAsync(second.Id, "primary"));

        Assert.Contains("single-valued", error.Message, StringComparison.Ordinal);
        Assert.Contains("SetAsync", error.Message, StringComparison.Ordinal);

        // And the first one is still the only holder.
        Assert.Equal(first.Id, (await harness.Media.For(product).GetByRoleAsync("primary"))?.Id);
    }

    [Fact]
    public async Task Re_adding_the_file_that_already_holds_a_single_role_is_still_a_no_op()
    {
        var harness = Declared();
        var product = new Product { Id = 1 };
        var file    = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png()));

        await harness.Media.For(product).AddAsync(file.Id, "primary");

        // Not an attempt at a second primary — it is the same one. A retried request must not fail.
        await harness.Media.For(product).AddAsync(file.Id, "primary");

        Assert.Single(harness.Store.Attachments);
    }

    [Fact]
    public async Task Setting_a_multi_valued_role_is_refused()
    {
        var harness = Declared();
        var product = new Product { Id = 1 };

        var a = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png(), "a.png"));
        var b = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png(32), "b.png"));

        await harness.Media.For(product).AddAsync(a.Id, "gallery");
        await harness.Media.For(product).AddAsync(b.Id, "gallery");

        var error = await Assert.ThrowsAsync<InvalidMediaException>(
            () => harness.Media.For(product).SetAsync("gallery", a.Id));

        Assert.Contains("multi-valued", error.Message, StringComparison.Ordinal);

        // The point of refusing: SetAsync would have detached b.png without a word.
        Assert.Equal(2, (await harness.Media.For(product).ListByRoleAsync("gallery")).Count);
    }

    [Fact]
    public async Task Asking_for_the_one_file_in_a_multi_valued_role_is_refused()
    {
        var harness = Declared();

        var error = await Assert.ThrowsAsync<InvalidMediaException>(
            () => harness.Media.For(new Product { Id = 1 }).GetByRoleAsync("gallery"));

        Assert.Contains("ListByRoleAsync", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reading_and_reordering_catch_a_misspelled_role_too()
    {
        var harness = Declared();
        var product = new Product { Id = 1 };

        await Assert.ThrowsAsync<InvalidMediaException>(
            () => harness.Media.For(product).ListByRoleAsync("galery"));

        await Assert.ThrowsAsync<InvalidMediaException>(
            () => harness.Media.For(product).ReorderAsync("galery", []));
    }

    [Fact]
    public async Task The_declared_roles_themselves_still_work()
    {
        var harness = Declared();
        var product = new Product { Id = 1 };

        var hero = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png(), "hero.png"));
        var one  = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png(32), "one.png"));
        var two  = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png(48), "two.png"));

        await harness.Media.For(product).SetAsync("primary", hero.Id);
        await harness.Media.For(product).AddAsync(one.Id, "gallery");
        await harness.Media.For(product).AddAsync(two.Id, "gallery");

        Assert.Equal(hero.Id, (await harness.Media.For(product).GetByRoleAsync("primary"))?.Id);
        Assert.Equal(["one.png", "two.png"],
            (await harness.Media.For(product).ListByRoleAsync("gallery")).Select(f => f.FileName));
    }

    /// <summary>
    /// The condition the whole feature rests on: declaring roles for one entity must not start
    /// policing every other one.
    /// </summary>
    [Fact]
    public async Task An_entity_that_declares_nothing_is_not_checked()
    {
        var harness = Declared();
        var file    = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png()));

        // Ticket has no attributes, so anything goes — including two files in one ad-hoc role.
        await harness.Media.For(new Ticket { Id = 1 }).AddAsync(file.Id, "whatever-i-like");

        var second = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png(32), "b.png"));
        await harness.Media.For(new Ticket { Id = 1 }).AddAsync(second.Id, "whatever-i-like");

        Assert.Equal(2, (await harness.Media.For(new Ticket { Id = 1 }).ListByRoleAsync("whatever-i-like")).Count);
    }

    [Fact]
    public async Task Nothing_is_checked_at_all_when_no_roles_were_declared()
    {
        var harness = Harness.Create();   // no registry
        var file    = await harness.Media.UploadAsync(TestMedia.Upload(TestMedia.Png()));

        // Even for Product, which carries the attributes: they are never read unless the
        // application asked for them at startup.
        await harness.Media.For(new Product { Id = 1 }).AddAsync(file.Id, "anything");

        Assert.Single(harness.Store.Attachments);
    }

    [Fact]
    public async Task The_roles_an_entity_declares_can_be_read_back()
    {
        var harness = Declared();

        Assert.Equal(
            [("primary", false), ("gallery", true)],
            harness.Media.RolesFor("Product").Select(r => (r.Name, r.Multiple)));

        Assert.Empty(harness.Media.RolesFor("Ticket"));

        await Task.CompletedTask;
    }
}

/// <summary>The builder methods that fill the registry.</summary>
public class RoleBuilderTests
{
    [MediaEntity("BuilderProduct")]
    [MediaRole("primary")]
    [MediaRole("gallery", Multiple = true)]
    private sealed class Product
    {
        public int Id { get; set; }
    }

    private sealed class Plain
    {
        public int Id { get; set; }
    }

    private static IMediaRoleRegistry Build(Action<MediaBuilder> configure)
    {
        var services = new ServiceCollection();
        var builder  = services.AddMedia();

        configure(builder);

        // Build() refuses without a store and a backend, so give it the in-memory doubles. Neither
        // is exercised here — this is about what the registry ends up holding.
        builder
            .UseCustomStore(_ => new InMemoryMediaStore())
            .UseCustomStorage("InMemory", _ => new InMemoryStorageProvider(), isDefault: true);

        return builder.Build().BuildServiceProvider().GetRequiredService<IMediaRoleRegistry>();
    }

    [Fact]
    public void DeclareRoles_of_a_type_reads_its_attributes()
    {
        var registry = Build(b => b.DeclareRoles<Product>());

        Assert.True(registry.Declares("BuilderProduct"));
        Assert.True(registry.Find("BuilderProduct", "gallery")!.Value.Multiple);
    }

    [Fact]
    public void DeclareRoles_of_a_type_without_any_says_so()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Build(b => b.DeclareRoles<Plain>()));

        Assert.Contains("declares no media roles", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Roles_can_be_declared_on_the_builder_for_a_type_you_cannot_annotate()
    {
        var registry = Build(b => b.DeclareRoles("Order", r => r.One("invoice").Many("packing-slip")));

        Assert.False(registry.Find("Order", "invoice")!.Value.Multiple);
        Assert.True(registry.Find("Order", "packing-slip")!.Value.Multiple);
    }

    [Fact]
    public void Scanning_an_assembly_finds_every_declaring_type()
    {
        var registry = Build(b => b.DeclareRolesFrom(typeof(RoleBuilderTests).Assembly));

        // Every type in this assembly carrying [MediaRole], each under its own stored name.
        Assert.True(registry.Declares("DiscoveryProduct"));
        Assert.True(registry.Declares("Product"));
        Assert.True(registry.Declares("BuilderProduct"));

        // And nothing else: the Product classes in the other test files carry no [MediaRole].
        Assert.False(registry.Declares("Ticket"));
    }

    [Fact]
    public void Declaring_one_entity_twice_is_refused()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Build(b => b
            .DeclareRoles<Product>()
            .DeclareRoles("BuilderProduct", r => r.One("primary"))));

        Assert.Contains("declared twice", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The order of these two lines must not matter. Reading the attributes when DeclareRolesFrom is
    /// called — rather than at Build — would file roles under whatever name resolution said at that
    /// moment, and a registry keyed on the wrong name silently checks nothing.
    /// </summary>
    [Fact]
    public void Declaring_before_or_after_the_rest_of_the_chain_gives_the_same_registry()
    {
        var first  = Build(b => b.DeclareRoles<Product>().WithMaxFileSize(1024));
        var second = Build(b => b.WithMaxFileSize(1024).DeclareRoles<Product>());

        Assert.Equal(first.RolesFor("BuilderProduct"), second.RolesFor("BuilderProduct"));
    }
}

/// <summary>Builds a registry over one type, the way the builder would.</summary>
internal static class Registry
{
    public static IMediaRoleRegistry Of<TEntity>() where TEntity : class =>
        new MediaRoleRegistry(new Dictionary<string, IReadOnlyList<MediaEntityRole>>(StringComparer.Ordinal)
        {
            [MediaEntityNames.For(typeof(TEntity))] = MediaEntityRoles.Of(typeof(TEntity))
        });
}
