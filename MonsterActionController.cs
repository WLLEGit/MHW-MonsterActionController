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
using HunterPie.Core.Definitions;
using HunterPie.Core.Enums;
using HunterPie.Core.Monsters;
using System.Text;

namespace HunterPie.Plugins.Example
{
    public class MonsterActionController : IPlugin
    {
        #region necessary api definition
        // This is your plugin name
        public string Name { get; set; } = "Monster Action Controller";

        // This is your plugin description, try to be as direct as possible on what your plugin does
        public string Description { get; set; } = "A plugin to enable you to control the action of monsters";

        // This is our game context, you'll use it to track in-game information and hook events
        public Game Context { get; set; }
        #endregion

        #region other definitions
        private long MonsterAddress { get; set; }
        private Thread thread;
        private Thread lockMountThread;
        private Thread lockHealthThread;
        private Monster targetMonster;

        private int selectedID = 0;
        private int maxID;

        bool is_debugging = false;
        bool is_health_locked = false;
        #endregion

        readonly List<string> actionDictID = new List<string>() { };
        readonly Dictionary<char, List<int>> cmdValues = new Dictionary<char, List<int>>() {
            { 'J', new List< int>(){} },
            { 'K', new List<int>() {} },
            { 'L', new List<int>() {} },
            { 'U', new List<int>() {} },
            { 'O', new List<int>() {} },
             { 'B', new List<int>() {} } ,
              { 'N', new List<int>() {} },
              { 'M', new List<int>() {} },
              { 'Z', new List<int>() {} },
              { 'V', new List<int>() {} },
              { 'P', new List<int>() {} } };


        #region funtions
        public static List<String[]> ReadCSV(string filePathName)
        {
            List<String[]> ls = new List<String[]>();
            StreamReader fileReader = new StreamReader(filePathName, Encoding.Default);
            string strLine = "";
            while (strLine != null)
            {
                strLine = fileReader.ReadLine();
                if (strLine != null && strLine.Length > 0)
                {
                    ls.Add(strLine.Split(','));
                }
            }
            fileReader.Close();
            return ls;
        }

        private void LoadConfig()
        {
            if (!File.Exists("Modules\\MonsterActionController\\actions.csv"))
            {
                this.Error("找不到HunterPie\\Modules\\MonsterActionController\\actions.csv配置文件！");
                this.Error("HunterPie\\Modules\\MonsterActionController\\actions.csv NOT FOUND!");
            }

            List<string[]> data = ReadCSV("Modules\\MonsterActionController\\actions.csv");
            int cnt = (data[0].Length - 1) / 2;
            for (int i = 0; i < cnt; ++i)
                actionDictID.Add(data[0][2 * i + 1]);
            for (int i = 1; i <= 11; ++i)
            {
                for (int j = 0; j < cnt; ++j)
                {
                    cmdValues[data[i][0][0]].Add(int.Parse(data[i][2 * j + 1]));
                }
            }
        }
        public void Initialize(Game context)
        {
            LoadConfig();
            Context = context;
            MonsterAddress = 0;
            maxID = actionDictID.Count;

            CreateHotkeys();
            HookEvents();
        }

        public void Unload()
        {
            RemoveHotkeys();

            UnhookEvents();
        }

        private void StopThread(bool stopLock = false)
        {
            if (thread == null)
                return;
            thread.Abort();
            if (stopLock)
            {
                lockMountThread.Abort();
            }
        }
        readonly int[] hotkeyIds = new int[16];
        public void CreateHotkeys()
        {
            hotkeyIds[0] = Hotkey.Register("Alt+J", () => { HotkeyCallback('J'); });
            hotkeyIds[1] = Hotkey.Register("Alt+K", () => { HotkeyCallback('K'); });
            hotkeyIds[2] = Hotkey.Register("Alt+L", () => { HotkeyCallback('L'); });
            hotkeyIds[3] = Hotkey.Register("Alt+U", () => { HotkeyCallback('U'); });
            hotkeyIds[4] = Hotkey.Register("Alt+I", () => { /*key conflict*/ });
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
                this.Log($"Transferring to: {actionDictID[selectedID]}");
            });

            hotkeyIds[8] = Hotkey.Register("Alt+S", () => { StopThread(true); });

            hotkeyIds[15] = Hotkey.Register("Alt+E", () => { LockMount(); });
        }

        public void HotkeyCallback(char cmd)
        {
            System.Media.SystemSounds.Beep.Play();
            LockStaminaAndHealth();
            StopThread();
            this.Log($"ALT+{cmd}");

            FieldInfo fieldInfo = typeof(Monster).GetField("monsterAddress", BindingFlags.Instance | BindingFlags.NonPublic);
            MonsterAddress = (long)fieldInfo.GetValue(Context.HuntedMonster);
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
            Context.FirstMonster.OnActionChange -= OnMonsterActionChangeCallBack;
            Context.SecondMonster.OnActionChange -= OnMonsterActionChangeCallBack;
            Context.ThirdMonster.OnActionChange -= OnMonsterActionChangeCallBack;
        }

        private Monster GetTargetMonster()
        {
            if (Context.HuntedMonster == null)
                return targetMonster;
            else
                return Context.HuntedMonster;
        }

        private void LockStaminaAndHealth()
        {
            if (is_health_locked)
                return;

            lockHealthThread = new Thread(() =>
            {
                this.Log("Health Locked");
                long address = Kernel.ReadMultilevelPtr(Address.GetAddress("BASE") + Address.GetAddress("EQUIPMENT_OFFSET"), Address.GetOffsets("PlayerBasicInformationOffsets"));
                float[] health;

                Kernel.Read<float>(address + 0x144);
                Kernel.Read<float>(address + 0x13C);

                while (true)
                {
                    float maxStamina = Kernel.Read<float>(address + 0x144);
                    float curStamina = Kernel.Read<float>(address + 0x13C);
                    if (maxStamina != curStamina)
                        Kernel.Write<float>(address + 0x13C, maxStamina - 10);

                    //health = Kernel.ReadStructure<float>(address + 0x60, 2);
                    //if(health[0] != health[1])
                    //    Kernel.Write<float>(address + 0x60 + 4, 99);
                }

            });
            lockHealthThread.Start();
            is_health_locked = true;
        }

        private void LockMount()
        {
            LockStaminaAndHealth();

            if (lockMountThread != null)
                lockMountThread.Abort();
            lockMountThread = new Thread(() =>
            {
                Ailment mountAil;
                mountAil = GetTargetMonster().Ailments[0];
                foreach (Ailment ailment in Context.HuntedMonster.Ailments)
                    if (ailment.Name == "骑乘")
                        mountAil = ailment;
                this.Log($"{mountAil.Buildup} / {mountAil.MaxBuildup}");
                long curBuildupAddr = mountAil.Address + sizeof(int) * 7 + sizeof(long) + sizeof(float) * 3;
                long maxBuildupAddr = mountAil.Address + sizeof(int) * 8 + sizeof(long) + sizeof(float) * 5;
                while (true)
                {
                    float maxBuildup = Kernel.Read<float>(maxBuildupAddr);
                    float curBuildup = Kernel.Read<float>(curBuildupAddr);
                    if (curBuildup != maxBuildup)
                        Kernel.Write<float>(curBuildupAddr, maxBuildup - 1);
                }
            });
            lockMountThread.Start();
        }
        private void OnMonsterActionChangeCallBack(object source, MonsterUpdateEventArgs args)
        {
            Monster tar = (Monster)source;
            targetMonster = tar;

            if (!is_debugging)
                return;
            using (StreamWriter sw = new StreamWriter("Actionlog.csv", true))
            {
                sw.WriteLine($"{tar.Name},{tar.ActionId},{tar.ActionReferenceName},{tar.ActionName}");
            }
        }
        #endregion
    }
}
