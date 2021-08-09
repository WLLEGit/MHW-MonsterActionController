using System;
using HunterPie.Core;
using HunterPie.Core.Input;
using HunterPie.Logger;
using HunterPie.Core.Events;
using System.IO;
using System.Reflection;
using HunterPie.Memory;
using System.Threading;
using System.Collections.Generic;

namespace HunterPie.Plugins.Example
{
    public class MonsterActionController : IPlugin
    {
        // This is your plugin name
        public string Name { get; set; } = "Monster Action Controller";

        // This is your plugin description, try to be as direct as possible on what your plugin does
        public string Description { get; set; } = "A plugin to enable you to control the action of monsters";

        // This is our game context, you'll use it to track in-game information and hook events
        public Game Context { get; set; }

        private long MonsterAddress { get; set; }
        private Thread thread;
        private Timer timer;

        private int selectedID = 0;
        private int maxID;
        readonly List<string> actionDictID_en = new List<string>() {"Fatalis" };
        readonly List<string> actionDictID_ch = new List<string>() {"黑龙" };
        readonly Dictionary<char, List<int>> cmdValues = new Dictionary<char, List<int>>() {
            { 'J', new List< int>(){88} },
            { 'K', new List<int>() {135} },
            { 'L', new List<int>() {154} },
            { 'U', new List<int>() {131} },
            { 'I', new List<int>() {131} },//unable to use
            { 'O', new List<int>() {78} },
             { 'B', new List<int>() {137} } ,
              { 'N', new List<int>() {76} },
              { 'M', new List<int>() {53} },
              { 'Z', new List<int>() {155} },
              { 'V', new List<int>() {81} },
              { 'P', new List<int>() {73} } };

        bool is_debugging = false;

        public void Initialize(Game context)
        {
            Context = context;
            MonsterAddress = 0;
            maxID = actionDictID_ch.Count;

            CreateHotkeys();
            HookEvents();
        }

        public void Unload()
        {
            RemoveHotkeys();

            UnhookEvents();
        }

        private void StopThread()
        {
            if(thread == null)
                return;
            thread.Abort();
        }
        readonly int[] hotkeyIds = new int[15];
        public void CreateHotkeys()
        {
            hotkeyIds[0] = Hotkey.Register("Alt+J", () => { HotkeyCallback('J'); });
            hotkeyIds[1] = Hotkey.Register("Alt+K", () => { HotkeyCallback('K'); });
            hotkeyIds[2] = Hotkey.Register("Alt+L", () => { HotkeyCallback('L'); });
            hotkeyIds[3] = Hotkey.Register("Alt+U", () => { HotkeyCallback('U'); });
            hotkeyIds[4] = Hotkey.Register("Alt+I", () => { HotkeyCallback('I'); });
            hotkeyIds[5] = Hotkey.Register("Alt+O", () => { HotkeyCallback('O'); });
            hotkeyIds[9] = Hotkey.Register("Alt+B", () => { HotkeyCallback('B'); });
            hotkeyIds[10] = Hotkey.Register("Alt+N", () => { HotkeyCallback('N'); });
            hotkeyIds[11] = Hotkey.Register("Alt+M", () => { HotkeyCallback('M'); });
            hotkeyIds[12] = Hotkey.Register("Alt+Z", () => { HotkeyCallback('Z'); });
            hotkeyIds[13] = Hotkey.Register("Alt+V", () => { HotkeyCallback('V'); });
            hotkeyIds[14] = Hotkey.Register("Alt+P", () => { HotkeyCallback('P'); });

            hotkeyIds[6] = Hotkey.Register("Alt+D", () =>
            {
                is_debugging = !is_debugging;
                this.Log($"is_debugging: {is_debugging}");
                using (StreamWriter sw = new StreamWriter("Actionlog.csv"))
                {
                    sw.WriteLine("Name,ActionID,ActionReferenceName,ActionName");
                }
            });

            hotkeyIds[7] = Hotkey.Register("Alt+T", () => {
                selectedID = (selectedID + 1) % maxID;
                this.Log($"Transferring to: {actionDictID_ch[selectedID]}/{actionDictID_en[selectedID]}");
            });

            hotkeyIds[8] = Hotkey.Register("Alt+S", () => { StopThread(); });
        }

        public void HotkeyCallback(char cmd)
        {
            StopThread();
			this.Log($"ALT+{cmd}");
            if (MonsterAddress == 0)
                return;
            long actionPointer = MonsterAddress + 0x61C8 + 0xB0;

            thread = new Thread(() =>
            {
                while (true)
                {
                    if (Kernel.Read<int>(actionPointer) != cmdValues[cmd][selectedID])
                        Kernel.Write<int>(actionPointer, cmdValues[cmd][selectedID]);
                }
            });
            thread.Start();
        }

        public void RemoveHotkeys()
        {
            foreach (int id in hotkeyIds)
                Hotkey.Unregister(id);
        }
        private void HookEvents()
        {
            // We can access the Player, Monsters and World from Context
            Context.FirstMonster.OnActionChange += OnMonsterActionChangeCallBack;
            Context.SecondMonster.OnActionChange += OnMonsterActionChangeCallBack;
            Context.ThirdMonster.OnActionChange += OnMonsterActionChangeCallBack;
        }

        private void UnhookEvents()
        {
            // To unhook events, we just do the same thing but with a minus instead of a plus
            Context.FirstMonster.OnActionChange += OnMonsterActionChangeCallBack;
            Context.SecondMonster.OnActionChange -= OnMonsterActionChangeCallBack;
            Context.ThirdMonster.OnActionChange -= OnMonsterActionChangeCallBack;
        }
        private void OnMonsterActionChangeCallBack(object source, MonsterUpdateEventArgs args)
        {
            Monster tar = (Monster)source;
            FieldInfo fieldInfo = typeof(Monster).GetField("monsterAddress", BindingFlags.Instance | BindingFlags.NonPublic);

            MonsterAddress = (long)fieldInfo.GetValue(tar);

            if (!is_debugging)
                return;
            using (StreamWriter sw = new StreamWriter("Actionlog.csv", true))
            {
                sw.WriteLine($"{tar.Name},{tar.ActionId},{tar.ActionReferenceName},{tar.ActionName}");
            }
        }

    }
}
