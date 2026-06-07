using Diploma.Core.Models;
using Diploma.Core.Services;

namespace AlgoReplay.Tests;

public class ModeProfileServiceTests
{
    private ModeProfileService _sut => new ModeProfileService();

    [Fact]
    public void Olympic_AllowsFileSaveAndRunOrDebug()
    {
        var p = _sut.GetProfile(RecordingMode.Olympic);
        Assert.True(p.IsAllowed(EventTypes.FileSave));
        Assert.True(p.IsAllowed(EventTypes.RunOrDebug));
    }

    [Fact]
    public void Olympic_DisallowsUndoAndFileSwitched()
    {
        var p = _sut.GetProfile(RecordingMode.Olympic);
        Assert.False(p.IsAllowed(EventTypes.Undo));
        Assert.False(p.IsAllowed(EventTypes.FileSwitched));
        Assert.False(p.IsAllowed(EventTypes.IdeClosed));
    }

    [Fact]
    public void Learning_AllowsUndo_Olympic_DoesNot()
    {
        Assert.False(_sut.GetProfile(RecordingMode.Olympic)
                         .IsAllowed(EventTypes.Undo));
        Assert.True(_sut.GetProfile(RecordingMode.Learning)
                        .IsAllowed(EventTypes.Undo));
    }

    [Fact]
    public void Personal_AllowsAllEventTypes()
    {
        var freshSut = new ModeProfileService();
        var p = freshSut.GetProfile(RecordingMode.Personal);
        var all = new[]
        {
            EventTypes.FileSave,     EventTypes.FileSavedAuto,
            EventTypes.RunOrDebug,   EventTypes.ClipboardCopy,
            EventTypes.IdleStart,    EventTypes.ManualMarker,
            EventTypes.Screenshot,   EventTypes.IdeOpened,
            EventTypes.FileSwitched
        };
        Assert.All(all, t => Assert.True(p.IsAllowed(t)));
    }

    [Fact]
    public void IsAllowed_UnknownType_ReturnsFalse()
    {
        var p = _sut.GetProfile(RecordingMode.Olympic);
        Assert.False(p.IsAllowed("UNKNOWN_XYZ"));
    }

    [Fact]
    public void UpdateProfile_Olympic_ThrowsInvalidOperation()
    {
        var p = _sut.GetProfile(RecordingMode.Olympic);
        Assert.Throws<InvalidOperationException>(
            () => _sut.UpdateProfile(p));
    }

    [Fact]
    public void UpdateProfile_Personal_Succeeds()
    {
        var p = _sut.GetProfile(RecordingMode.Personal);
        p.AllowedEvents.Remove(EventTypes.Undo);
        _sut.UpdateProfile(p);
        Assert.False(_sut.GetProfile(RecordingMode.Personal)
                         .IsAllowed(EventTypes.Undo));
    }

    [Theory]
    [InlineData(RecordingMode.Olympic,  false)]
    [InlineData(RecordingMode.Learning, true)]
    [InlineData(RecordingMode.Work,     true)]
    [InlineData(RecordingMode.Personal, true)]
    public void IsCustomizable_MatchesExpected(
        RecordingMode mode, bool expected)
    {
        Assert.Equal(expected, _sut.GetProfile(mode).IsCustomizable);
    }
}