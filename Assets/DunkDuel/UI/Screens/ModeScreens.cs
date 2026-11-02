using System;
using System.IO;
using DunkDuel.AI;
using DunkDuel.Presentation;
using DunkDuel.Sim;
using DunkDuel.Sim.Data;
using DunkDuel.Sim.Session;
using UnityEngine;
using UnityEngine.UI;

namespace DunkDuel.UI
{
    /// <summary>Court School: interactive lessons on the Read, shakes, timing, Hype and Denial.</summary>
    public sealed class CourtSchoolScreen : TitledScreen
    {
        protected override void Build(RectTransform root)
        {
            Frame(root, Loc.T("school.title", "Court School"));
            ScrollRect scroll = UIKit.Scroll(Body, "Lessons", out RectTransform content);
            UIKit.Stretch(scroll.GetComponent<RectTransform>());
            LessonDef[] lessons = LessonDef.All();
            for (int i = 0; i < lessons.Length; i++)
            {
                LessonDef l = lessons[i];
                bool passed = PlayerPrefs.GetInt("dd.school." + l.Id, 0) == 1;
                Button b = UIKit.Button(content, (passed ? "[x] " : "[ ] ") + l.Title, () => Start(l), 1700, 100, Theme.PanelLight, 34);
                UIKit.Layout(b, 1700, 100);
            }
        }

        private void Start(LessonDef l)
        {
            int fighter = App.LastFighter >= 0 ? App.LastFighter : 0;
            int opponent = (fighter + 1) % App.Tables.FighterCount;
            App.StartMatch(new MatchRequest
            {
                Kind = MatchKind.School,
                Fighter0 = fighter,
                Fighter1 = opponent,
                Court = App.Data.CourtIndex("rivalry_night"),
                RoundsToWin = 99,
                FirstOffense = l.PlayerStartsWithBall ? 0 : 1,
                InfiniteHype = l.InfiniteHype,
                Lesson = l,
            });
        }
    }

    /// <summary>Road to The Crown: six matches from the streets to the finals with a story beat before each.</summary>
    public sealed class RoadToCrownScreen : TitledScreen
    {
        private readonly int _fighter;

        public RoadToCrownScreen(int fighter)
        {
            _fighter = fighter;
        }

        protected override void Build(RectTransform root)
        {
            SimTables t = App.Tables;
            CharacterDefinition def = t.Fighters[_fighter].Def;
            Frame(root, Loc.T("crown.title", "Road to The Crown") + "  -  " + def.Nickname.ToUpperInvariant());
            int stage = App.Ladder.StageFor(def.Id);

            RectTransform ladder = UIKit.VStack(Body, "Ladder", 12, 0, TextAnchor.UpperLeft);
            UIKit.Place(ladder, new Vector2(0f, 1f), new Vector2(0f, 1f), Vector2.zero, new Vector2(760f, 860f));
            for (int i = 0; i < LadderProgress.Stages.Length; i++)
            {
                LadderProgress.Stage st = LadderProgress.Stages[i];
                CourtDef court = App.Data.Court(App.Data.CourtIndex(st.Court));
                int opp = LadderProgress.Opponent(t, _fighter, i);
                string label = (i + 1) + ".  " + court.Name + "  vs " + t.Fighters[opp].Def.Nickname + "  (" + st.Tier + ")";
                Color c = i < stage ? Theme.Good * 0.6f + Color.black * 0.4f : (i == stage ? Theme.Accent * 0.6f + Color.black * 0.4f : Theme.Panel);
                Button b = UIKit.Button(ladder, label, null, 740, 96, c, 28);
                b.interactable = i == stage;
            }

            // Story beat card for the next stage (illustrated panels come with final art; text for now).
            Image card = UIKit.Panel(Body, "Story", Theme.Panel);
            UIKit.Place(card.rectTransform, new Vector2(1f, 1f), new Vector2(1f, 1f), Vector2.zero, new Vector2(960f, 700f));
            string beat = Story(def.Id, stage);
            UIText text = UIKit.Label(card.transform, beat, 34, TextAlign.TopLeft, Theme.Text);
            UIKit.Stretch(text.Rect, 36, 36, 36, 36);
            if (App.Ladder.Completed(def.Id))
            {
                UIText crown = UIKit.Label(card.transform, Loc.T("crown.champion", "CHAMPION"), 44, TextAlign.Bottom, Theme.Accent, true);
                UIKit.Stretch(crown.Rect, 36, 36, 36, 36);
            }
            Button play = UIKit.Button(Body, Loc.T("crown.play", "Play stage") + " " + (stage + 1), () => Play(stage), 500, 110, Theme.Accent * 0.7f + Color.black * 0.3f);
            UIKit.Place((RectTransform)play.transform, new Vector2(1f, 0f), new Vector2(1f, 0f), Vector2.zero, new Vector2(500f, 110f));
        }

        private static string Story(string fighterId, int stage)
        {
            JsonValue root = PJson.Load(PresentationData.Root + "story");
            JsonValue f = root?.Get(fighterId);
            if (f == null || !f.IsArray || f.Items.Count == 0) return "";
            int i = Mathf.Clamp(stage, 0, f.Items.Count - 1);
            return Loc.T("story." + fighterId + "." + i, f.Items[i].Text);
        }

        private void Play(int stage)
        {
            LadderProgress.Stage st = LadderProgress.Stages[stage];
            App.StartMatch(new MatchRequest
            {
                Kind = MatchKind.Ladder,
                Fighter0 = _fighter,
                Fighter1 = LadderProgress.Opponent(App.Tables, _fighter, stage),
                Court = App.Data.CourtIndex(st.Court),
                Tier = st.Tier,
                RoundsToWin = 2,
                LadderStage = stage,
            });
        }
    }

    /// <summary>Saved matches (inputs only, a few kilobytes each): list, play back, delete.</summary>
    public sealed class ReplaysScreen : TitledScreen
    {
        public static string Folder => Path.Combine(Application.persistentDataPath, "replays");
        private RectTransform _content;

        protected override void Build(RectTransform root)
        {
            Frame(root, Loc.T("replays.title", "Replays"));
            ScrollRect scroll = UIKit.Scroll(Body, "List", out _content);
            UIKit.Stretch(scroll.GetComponent<RectTransform>());
            Refresh();
        }

        private void Refresh()
        {
            for (int i = _content.childCount - 1; i >= 0; i--) UnityEngine.Object.Destroy(_content.GetChild(i).gameObject);
            if (!Directory.Exists(Folder))
            {
                UIKit.Label(_content, Loc.T("replays.none", "No saved replays yet. Save one from the results screen."), 34, TextAlign.Left, Theme.Dim);
                return;
            }
            string[] files = Directory.GetFiles(Folder, "*.ddreplay");
            Array.Sort(files, StringComparer.Ordinal);
            Array.Reverse(files);
            if (files.Length == 0) UIKit.Label(_content, Loc.T("replays.none", "No saved replays yet."), 34, TextAlign.Left, Theme.Dim);
            foreach (string path in files)
            {
                string p = path;
                RectTransform row = UIKit.HStack(_content, "Row", 16, 0, TextAnchor.MiddleLeft);
                UIKit.Layout(row, 1700, 100);
                UIKit.Button(row, Path.GetFileNameWithoutExtension(p), () => Play(p), 1300, 96, Theme.PanelLight, 28);
                UIKit.Button(row, Loc.T("replays.delete", "Delete"), () =>
                {
                    try
                    {
                        File.Delete(p);
                    }
                    catch (IOException)
                    {
                    }
                    Refresh();
                }, 300, 96, new Color(0.4f, 0.14f, 0.14f, 1f), 28);
            }
        }

        private void Play(string path)
        {
            Replay r;
            try
            {
                r = Replay.FromBytes(File.ReadAllBytes(path));
            }
            catch (Exception e)
            {
                App.Toast(Loc.T("replays.bad", "Could not read replay") + ": " + e.Message);
                return;
            }
            if (r.SimVersion != SimBuildInfo.Version(App.Tables))
            {
                App.Toast(Loc.T("replays.version", "This replay was recorded on a different game version."));
                return;
            }
            App.StartMatch(new MatchRequest { Kind = MatchKind.Replay, Replay = r, Court = r.Setup.CourtIndex });
        }
    }
}
