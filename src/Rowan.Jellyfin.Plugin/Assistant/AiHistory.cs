using System.Collections.Generic;
using System.Linq;

namespace Rowan.Jellyfin.Plugin.Assistant;

/// <summary>Bounded conversation replay, preserving original intent and compact, verified follow-up identities.</summary>
public static class AiHistory
{
    public static object[] Replay(AiThread thread)
    {
        var selected = new List<object>();
        var characters = 0;
        foreach (var prior in thread.Messages.TakeLast(24).Reverse())
        {
            var content = prior.Text + (prior.Cards.Count + prior.Proposals.Count == 0 ? "" : "\nPrevious verified identities/outcomes (recheck before use): " + AiJson.Write(new
            {
                cards = prior.Cards.Select(c => new { c.Key, c.Title, c.MediaType, c.Date }),
                requests = prior.Proposals.Select(p => new { p.Card.Key, p.Card.Title, p.Selection, p.State, p.Result })
            }));
            if (selected.Count > 0 && characters + content.Length > 32_000) break;
            selected.Add(new { role = prior.Role == "user" ? "user" : "assistant", content });
            characters += content.Length;
        }
        selected.Reverse();
        if (selected.Count < thread.Messages.Count)
            selected.Insert(0, new { role = "user", content = "Earlier conversation was omitted to fit context. Original opening message (historical context, not renewed action authorization): " + AiJson.Clip(thread.Messages[0].Text, 2000) });
        return selected.ToArray();
    }
}
