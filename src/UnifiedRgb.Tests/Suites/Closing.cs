using UnifiedRgb.Core;

namespace UnifiedRgb.Tests;

/*-----------------------------------------------------------*\
| Closing the app, when closing it would lose work.            |
|                                                              |
| Four outcomes, and each wrong one is expensive: discard the   |
| user's edits, or leave a window that will not close and looks |
| like a hang. None of it could be tested while it lived in a   |
| Closing handler next to a modal and a CancelEventArgs.        |
|                                                              |
| It has cost real data once already. A logoff ran the ordinary |
| prompt flow, and because nobody can answer a modal while      |
| Windows is tearing the session down, a show step was written  |
| into the user's profile. So "the session is ending" is an     |
| INPUT to the decision here, not a special case buried in a    |
| handler where the next person can miss it.                    |
\*-----------------------------------------------------------*/
static class ClosingSuite
{
    public static void Run(Harness t)
    {
        t.Section("what to ask on the way out");
        t.Equal(ClosePrompt.None,
            CloseDecision.WhatToAsk(sessionEnding: false, needsFirstProfile: false, hasUnsavedChanges: false),
            "nothing unsaved: close without a word");
        t.Equal(ClosePrompt.SaveChanges,
            CloseDecision.WhatToAsk(sessionEnding: false, needsFirstProfile: false, hasUnsavedChanges: true),
            "unsaved changes: offer to save them");
        t.Equal(ClosePrompt.FirstProfile,
            CloseDecision.WhatToAsk(sessionEnding: false, needsFirstProfile: true, hasUnsavedChanges: true),
            "no profile at all: offer to make one, which wins over the ordinary prompt");

        t.Section("a session ending is never prompted");
        // The one that cost data. Windows is going down, the state is already
        // saved on that path, and a modal nobody can answer is how the WRONG
        // thing gets written.
        t.Equal(ClosePrompt.None,
            CloseDecision.WhatToAsk(sessionEnding: true, needsFirstProfile: false, hasUnsavedChanges: true),
            "logging off does not ask about unsaved changes");
        t.Equal(ClosePrompt.None,
            CloseDecision.WhatToAsk(sessionEnding: true, needsFirstProfile: true, hasUnsavedChanges: true),
            "...nor about a first profile, however much there is to say");

        t.Section("what the answer means");
        t.Equal(CloseAction.StayOpen, CloseDecision.Resolve(CloseAnswer.Cancel, saved: false),
            "Cancel means stay open");
        t.Equal(CloseAction.StayOpen, CloseDecision.Resolve(CloseAnswer.Cancel, saved: true),
            "...even if something did get saved on the way: they said no");
        t.Equal(CloseAction.Close, CloseDecision.Resolve(CloseAnswer.Discard, saved: false),
            "choosing not to save closes, and the save result is irrelevant");
        t.Equal(CloseAction.Close, CloseDecision.Resolve(CloseAnswer.Save, saved: true),
            "saved: close");

        t.Section("a save that was asked for and did not happen");
        // Closing here would throw away the very work they had just asked to
        // keep. And it has to SAY so: a window that silently refuses to close
        // looks like a hang, and from the tray there is nothing on screen.
        t.Equal(CloseAction.StayOpenAndExplain, CloseDecision.Resolve(CloseAnswer.Save, saved: false),
            "the one case that stays open AND explains itself");
        t.Check(CloseDecision.Resolve(CloseAnswer.Save, saved: false) != CloseAction.StayOpen,
            "...and is distinguishable from the user simply cancelling, or the notice never appears");
    }
}
