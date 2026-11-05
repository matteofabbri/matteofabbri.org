using TheQult.Data;
using TheQult.Services;

namespace TheQult.Tests;

// Le regole della community, provate su uno store Sperone vero (cartella temp).
public class CommunityServiceTests
{
    private static async Task<Member> JoinAsync(CommunityService community, string email, string city = "Rome", string hosting = Member.HostingHost)
    {
        var result = await community.JoinAsync(TestHost.Join(email: email, city: city, hosting: hosting));
        Assert.True(result.Ok, result.Error);
        return result.Value!;
    }

    private static DateOnly Day(int offset) =>
        DateOnly.FromDateTime(DateTime.UtcNow).AddDays(offset);

    [Fact]
    public async Task Join_ThenLoginFindsTheMember_RegardlessOfEmailCase()
    {
        using var store = TestHost.CreateStore();
        var community = TestHost.CreateCommunity(store);

        var joined = await JoinAsync(community, "Matteo@Example.COM");

        var found = await store.GetMemberByEmailAsync("  matteo@example.com ");
        Assert.NotNull(found);
        Assert.Equal(joined.Id, found.Id);
    }

    [Fact]
    public async Task Join_RejectsADuplicateEmail()
    {
        using var store = TestHost.CreateStore();
        var community = TestHost.CreateCommunity(store);

        await JoinAsync(community, "matteo@example.com");
        var again = await community.JoinAsync(TestHost.Join(email: "matteo@example.com"));

        Assert.False(again.Ok);
        Assert.Equal("email-taken", again.Error);
    }

    [Fact]
    public async Task RequestStay_RejectsSelfAndNotHostingHostsAndBadDates()
    {
        using var store = TestHost.CreateStore();
        var community = TestHost.CreateCommunity(store);
        var guest = await JoinAsync(community, "guest@example.com");
        var notHosting = await JoinAsync(community, "busy@example.com", hosting: Member.HostingNo);
        var host = await JoinAsync(community, "host@example.com");

        Assert.Equal("self", (await community.RequestStayAsync(guest.Id, guest.Id, Day(5), Day(7), "hi")).Error);
        Assert.Equal("host-not-found", (await community.RequestStayAsync(guest.Id, "missing", Day(5), Day(7), "hi")).Error);
        Assert.Equal("host-not-hosting", (await community.RequestStayAsync(guest.Id, notHosting.Id, Day(5), Day(7), "hi")).Error);
        Assert.Equal("dates", (await community.RequestStayAsync(guest.Id, host.Id, Day(7), Day(7), "hi")).Error);
        Assert.Equal("past", (await community.RequestStayAsync(guest.Id, host.Id, Day(-1), Day(2), "hi")).Error);
        Assert.Equal("too-long", (await community.RequestStayAsync(guest.Id, host.Id, Day(1), Day(1).AddDays(31), "hi")).Error);
    }

    [Fact]
    public async Task RequestStay_ThenAcceptByHost_MakesTheStay()
    {
        using var store = TestHost.CreateStore();
        var community = TestHost.CreateCommunity(store);
        var guest = await JoinAsync(community, "guest@example.com");
        var host = await JoinAsync(community, "host@example.com");

        var requested = await community.RequestStayAsync(guest.Id, host.Id, Day(5), Day(7), "Hi, I arrive Friday!");
        Assert.True(requested.Ok, requested.Error);
        Assert.Equal(StayRequest.StatusPending, requested.Value!.Status);

        // L'ospite non può accettare la propria richiesta.
        Assert.Equal("forbidden", (await community.ChangeStayStatusAsync(guest.Id, requested.Value.Id, "accept")).Error);

        var accepted = await community.ChangeStayStatusAsync(host.Id, requested.Value.Id, "accept");
        Assert.True(accepted.Ok, accepted.Error);
        Assert.Equal(StayRequest.StatusAccepted, accepted.Value!.Status);

        // Chiusa è chiusa: nessuna seconda decisione.
        Assert.Equal("closed", (await community.ChangeStayStatusAsync(host.Id, requested.Value.Id, "decline")).Error);

        var involving = await store.GetStaysInvolvingAsync(guest.Id);
        Assert.Single(involving);
    }

    [Fact]
    public async Task GuestCanCancelOnlyWhilePending()
    {
        using var store = TestHost.CreateStore();
        var community = TestHost.CreateCommunity(store);
        var guest = await JoinAsync(community, "guest@example.com");
        var host = await JoinAsync(community, "host@example.com");

        var stay = (await community.RequestStayAsync(guest.Id, host.Id, Day(3), Day(5), "hi")).Value!;

        // L'host non può "cancellare" la richiesta di un ospite.
        Assert.Equal("forbidden", (await community.ChangeStayStatusAsync(host.Id, stay.Id, "cancel")).Error);

        var cancelled = await community.ChangeStayStatusAsync(guest.Id, stay.Id, "cancel");
        Assert.True(cancelled.Ok, cancelled.Error);
        Assert.Equal(StayRequest.StatusCancelled, cancelled.Value!.Status);
    }

    [Fact]
    public async Task References_RequireAnAcceptedStay_AndComeOncePerDirection()
    {
        using var store = TestHost.CreateStore();
        var community = TestHost.CreateCommunity(store);
        var guest = await JoinAsync(community, "guest@example.com");
        var host = await JoinAsync(community, "host@example.com");

        var stay = (await community.RequestStayAsync(guest.Id, host.Id, Day(2), Day(4), "hi")).Value!;

        // Prima che lo stay sia accettato, niente referenze.
        Assert.Equal("stay-not-accepted",
            (await community.WriteReferenceAsync(guest.Id, stay.Id, Reference.VerdictPositive, "Great host!")).Error);

        await community.ChangeStayStatusAsync(host.Id, stay.Id, "accept");

        // Un estraneo non può scrivere.
        var stranger = await JoinAsync(community, "stranger@example.com");
        Assert.Equal("forbidden",
            (await community.WriteReferenceAsync(stranger.Id, stay.Id, Reference.VerdictPositive, "I was not there")).Error);

        // L'ospite scrive per l'host: funziona, e la seconda volta no.
        var byGuest = await community.WriteReferenceAsync(guest.Id, stay.Id, Reference.VerdictPositive, "Wonderful couch, better company.");
        Assert.True(byGuest.Ok, byGuest.Error);
        Assert.Equal(host.Id, byGuest.Value!.SubjectId);

        Assert.Equal("duplicate",
            (await community.WriteReferenceAsync(guest.Id, stay.Id, Reference.VerdictPositive, "Again")).Error);

        // L'host può comunque scrivere la sua (l'altra direzione).
        var byHost = await community.WriteReferenceAsync(host.Id, stay.Id, Reference.VerdictNeutral, "Left the place tidy.");
        Assert.True(byHost.Ok, byHost.Error);
        Assert.Equal(guest.Id, byHost.Value!.SubjectId);

        var aboutHost = await store.GetReferencesForAsync(host.Id);
        Assert.Single(aboutHost);
        Assert.Equal("Wonderful couch, better company.", aboutHost[0].Text);
    }

    [Fact]
    public async Task UpdateProfile_ChangesHostingAndCity_LeavesEmailAlone()
    {
        using var store = TestHost.CreateStore();
        var community = TestHost.CreateCommunity(store);
        var member = await JoinAsync(community, "matteo@example.com");

        var updated = await community.UpdateProfileAsync(member.Id, new CommunityService.ProfileInput(
            DisplayName: "Matteo F.", City: "Lisbon", Country: "pt", Hosting: Member.HostingMaybe,
            MaxGuests: 3, Languages: "Italiano", About: "A couch by the river."));

        Assert.True(updated.Ok, updated.Error);
        Assert.Equal("Lisbon", updated.Value!.City);
        Assert.Equal("PT", updated.Value.Country);
        Assert.Equal(Member.HostingMaybe, updated.Value.Hosting);
        Assert.Equal("matteo@example.com", updated.Value.Email);
    }

    [Fact]
    public async Task ListMembers_FiltersByCityAndHosting()
    {
        using var store = TestHost.CreateStore();
        var community = TestHost.CreateCommunity(store);
        await JoinAsync(community, "rome-host@example.com", city: "Rome", hosting: Member.HostingHost);
        await JoinAsync(community, "rome-busy@example.com", city: "rome", hosting: Member.HostingNo);
        await JoinAsync(community, "lisbon@example.com", city: "Lisbon", hosting: Member.HostingHost);

        var inRome = await store.ListMembersAsync(city: "  ROME  ", hosting: null, page: 1);
        Assert.Equal(2, inRome.Total);

        var hostsInRome = await store.ListMembersAsync(city: "Rome", hosting: Member.HostingHost, page: 1);
        Assert.Single(hostsInRome.Items);
        Assert.Equal("rome-host@example.com", hostsInRome.Items[0].Email);

        var nowhere = await store.ListMembersAsync(city: "Naples", hosting: null, page: 1);
        Assert.Equal(0, nowhere.Total);
    }
}
