namespace UnifiedRgb.Core;

/// <summary>What the app should ask on the way out, if anything.</summary>
public enum ClosePrompt
{
    /// <summary>Nothing to ask: close.</summary>
    None,
    /// <summary>No profile exists at all - offer to make one, name box included.</summary>
    FirstProfile,
    /// <summary>The active profile has unsaved changes.</summary>
    SaveChanges,
}

/// <summary>What the user said to that prompt.</summary>
public enum CloseAnswer { Save, Discard, Cancel }

/// <summary>What to do once the answer is in and any save has been attempted.</summary>
public enum CloseAction
{
    /// <summary>Let the window close.</summary>
    Close,
    /// <summary>Stay open. The user asked to - nothing more to say.</summary>
    StayOpen,
    /// <summary>Stay open AND say why: they asked to save, and the save did not
    /// happen. Closing anyway would throw away the very work they just asked to
    /// keep, and a window that silently refuses to close looks like a hang -
    /// from the tray there would be nothing on screen at all.</summary>
    StayOpenAndExplain,
}

/// <summary>Whether closing loses work, and what to do about it.
///
/// Pure, and separate from the window, for the same reason AutomationDecision
/// is separate from AutomationService: the WINDOW owns the modal, the dispatcher
/// and the CancelEventArgs, and none of those can be driven from a test. What
/// can go wrong here is not the dialog, it is the judgement - four outcomes,
/// and getting one wrong either throws away the user's work or leaves an app
/// that will not close.
///
/// This path has already cost real data: a logoff used to run the ordinary
/// prompt flow, and because nobody can answer a modal while Windows is tearing
/// the session down, a show step got written into the user's profile. That is
/// why SessionEnding is an input here and not a special case buried in a
/// handler - see WhatToAsk.</summary>
public static class CloseDecision
{
    /// <param name="sessionEnding">Windows is logging off or shutting down. A
    /// modal would never be answered, and the state has already been saved on
    /// that path, so asking is not just useless - it is how the wrong thing
    /// gets written.</param>
    public static ClosePrompt WhatToAsk(bool sessionEnding, bool needsFirstProfile, bool hasUnsavedChanges)
    {
        if (sessionEnding) return ClosePrompt.None;
        if (needsFirstProfile) return ClosePrompt.FirstProfile;
        return hasUnsavedChanges ? ClosePrompt.SaveChanges : ClosePrompt.None;
    }

    /// <param name="saved">The result of the save the user asked for. Ignored
    /// unless they asked for one, so a caller that did not attempt a save can
    /// pass anything.</param>
    public static CloseAction Resolve(CloseAnswer answer, bool saved) => answer switch
    {
        CloseAnswer.Cancel => CloseAction.StayOpen,
        // Asked to save, and it did not happen: a full disk, a settings folder
        // another program has open. Keep the editing session up for another try.
        CloseAnswer.Save when !saved => CloseAction.StayOpenAndExplain,
        _ => CloseAction.Close,
    };
}
