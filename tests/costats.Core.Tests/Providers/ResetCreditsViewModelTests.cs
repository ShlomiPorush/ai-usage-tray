using costats.App.ViewModels;
using costats.Infrastructure.Providers;
using Xunit;

namespace costats.Core.Tests.Providers;

public sealed class ResetCreditsViewModelTests
{
    [Fact]
    public void English_ui_keeps_dates_readable_under_a_Hebrew_system_culture()
    {
        var original = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new("he-IL");
            var row = new ResetCreditRow(ResetClientFake.Credit("test") with
            {
                GrantedAt = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero),
                ExpiresAt = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero)
            });
            Assert.StartsWith("Granted Sep 4, 2026", row.GrantedText);
            Assert.StartsWith("Expires Oct 4, 2026", row.ExpiryText);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = original; }
    }

    [Fact]
    public async Task Loading_and_reviewing_do_not_consume_and_selection_change_cancels_confirmation()
    {
        var fake = new ResetClientFake();
        var vm = Create(fake);
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.Credits.Count);
        Assert.Null(vm.SelectedCredit);
        Assert.False(vm.ReviewCommand.CanExecute(null));
        vm.SelectedCredit = vm.Credits[1];
        vm.ReviewCommand.Execute(null);
        Assert.True(vm.IsConfirming);
        Assert.Contains("Account B", vm.ConfirmationText);
        Assert.Contains("Reset second", vm.ConfirmationText);
        Assert.Empty(fake.Calls);
        vm.SelectedCredit = vm.Credits[0];
        Assert.False(vm.IsConfirming);
        Assert.False(vm.ConfirmCommand.CanExecute(null));
    }

    [Fact]
    public async Task Partial_bank_is_shown_but_cannot_be_used()
    {
        var fake = new ResetClientFake { Snapshot = ResetClientFake.FullBank() with { ResetCreditsAvailable = 3 } };
        var vm = Create(fake);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedCredit = vm.Credits[0];
        Assert.Contains("2 of 3", vm.Summary);
        Assert.False(vm.ReviewCommand.CanExecute(null));
        await vm.ConfirmCommand.ExecuteAsync(null);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task Confirm_locks_interaction_and_refreshes_bank_and_usage()
    {
        var fake = new ResetClientFake { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var refreshes = 0;
        var vm = new ResetCreditsViewModel("Account B", "account-b", new(fake, fake), () => { refreshes++; return Task.CompletedTask; });
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedCredit = vm.Credits[1];
        vm.ReviewCommand.Execute(null);
        var confirmation = vm.ConfirmCommand.ExecuteAsync(null);
        Assert.True(vm.IsBusy);
        Assert.False(vm.CanInteract);
        Assert.False(vm.RefreshCommand.CanExecute(null));
        Assert.False(vm.ConfirmCommand.CanExecute(null));
        fake.Snapshot = fake.Snapshot! with { ResetCreditsAvailable = 1, ResetCredits = [ResetClientFake.Credit("first")] };
        fake.Pending.SetResult(ResetCreditOutcome.Reset);
        await confirmation;
        Assert.False(vm.IsBusy);
        Assert.Null(vm.SelectedCredit);
        Assert.Single(vm.Credits);
        Assert.Equal(1, refreshes);
        Assert.Equal("second", Assert.Single(fake.Calls).Credit);
        Assert.Contains("Reset used", vm.Status);
    }

    [Fact]
    public async Task Failed_refresh_clears_stale_selection_and_bank()
    {
        var fake = new ResetClientFake();
        var vm = Create(fake);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedCredit = vm.Credits[0];
        vm.ReviewCommand.Execute(null);
        fake.Snapshot = null;
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Empty(vm.Credits);
        Assert.Null(vm.SelectedCredit);
        Assert.False(vm.IsConfirming);
        Assert.False(vm.ReviewCommand.CanExecute(null));
        Assert.Empty(fake.Calls);
    }

    private static ResetCreditsViewModel Create(ResetClientFake fake) =>
        new("Account B", "account-b", new(fake, fake), () => Task.CompletedTask);
}
