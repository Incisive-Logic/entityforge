using EntityForge.Db.AppModel.Connections;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EntityForge.Db.Studio.Core.Tests;

[TestClass]
public sealed class ConnectionProfilesTests
{
    [TestMethod]
    public async Task Edits_and_removal_use_identity_and_preserve_other_profiles()
    {
        var store = new MemoryStore();
        var profiles = new EntityForgeConnectionProfiles(store);
        await profiles.SaveAsync(new("one", "Same name", new("https://one.test")));
        await profiles.SaveAsync(new("two", "Same name", new("https://two.test")));
        await profiles.SaveAsync(new("one", "Edited", new("https://edited.test")));
        Assert.AreEqual(2, store.Profiles.Count);
        Assert.AreEqual("Edited", store.Profiles.Single(p => p.Id == "one").Name);
        await profiles.RemoveAsync("one");
        Assert.AreEqual("two", store.Profiles.Single().Id);
    }

    [TestMethod]
    public async Task Unreadable_preferences_are_not_overwritten()
    {
        var store = new MemoryStore { FailRead = true };
        var profiles = new EntityForgeConnectionProfiles(store);
        await Assert.ThrowsExactlyAsync<IOException>(async () => await profiles.SaveAsync(new("one", "One", new("https://one.test"))));
        await Assert.ThrowsExactlyAsync<IOException>(async () => await profiles.RemoveAsync("one"));
        Assert.AreEqual(0, store.Writes);
    }

    private sealed class MemoryStore : IEntityForgeConnectionProfileStore
    {
        public IReadOnlyList<EntityForgeConnectionProfile> Profiles { get; private set; } = [];

        public bool FailRead
        {
            get; init;
        }

        public int Writes
        {
            get; private set;
        }

        public ValueTask<IReadOnlyList<EntityForgeConnectionProfile>> LoadAsync(CancellationToken cancellationToken = default) =>
            FailRead ? throw new IOException("Unreadable preferences") : ValueTask.FromResult(Profiles);

        public ValueTask SaveAsync(IReadOnlyList<EntityForgeConnectionProfile> profiles, CancellationToken cancellationToken = default)
        {
            Writes++;
            Profiles = profiles;
            return ValueTask.CompletedTask;
        }
    }
}
