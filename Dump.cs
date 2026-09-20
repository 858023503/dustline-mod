using System;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx;
using UnityEngine;

namespace DustlineDump
{
    // 反射 dump 插件：
    // 因为我没有反编译器，所以让游戏自己在运行时把 Dustline 自己的程序集
    // 里的类型/方法/字段全导出来，我据此定位敌人、玩家、武器、联机状态等类。
    [BepInPlugin("dsh.dustline.dump", "Dustline Reflection Dump", "1.0.0")]
    public class DumpPlugin : BaseUnityPlugin
    {
        private static readonly string OutPath = @"D:\cs\_mod\dump_types.txt";
        private static readonly string TargetNames = "|Dustline.Core|Dustline.Runtime|Assembly-CSharp|";

        void Awake()
        {
            try
            {
                Dump();
            }
            catch (Exception e)
            {
                Debug.LogError("[Dump] FAILED: " + e.ToString());
                try
                {
                    File.WriteAllText(@"D:\cs\_mod\dump_error.txt", e.ToString(), Encoding.UTF8);
                }
                catch { }
            }
        }

        private void Dump()
        {
            StringBuilder sb = new StringBuilder();
            Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();

            sb.AppendLine("=== LOADED ASSEMBLIES: " + asms.Length + " ===");
            for (int i = 0; i < asms.Length; i++)
            {
                sb.AppendLine("  " + asms[i].GetName().Name + "  v" + asms[i].GetName().Version);
            }
            sb.AppendLine();

            int typeCount = 0;
            for (int ai = 0; ai < asms.Length; ai++)
            {
                Assembly a = asms[ai];
                string an = a.GetName().Name;
                if (TargetNames.IndexOf("|" + an + "|") < 0) continue;

                sb.AppendLine();
                sb.AppendLine("################ ASSEMBLY: " + an + " ################");

                Type[] types;
                try { types = a.GetTypes(); }
                catch (ReflectionTypeLoadException ex)
                {
                    System.Collections.Generic.List<Type> keep = new System.Collections.Generic.List<Type>();
                    for (int k = 0; k < ex.Types.Length; k++) if (ex.Types[k] != null) keep.Add(ex.Types[k]);
                    types = keep.ToArray();
                }
                catch (Exception) { continue; }

                for (int ti = 0; ti < types.Length; ti++)
                {
                    Type t = types[ti];
                    if (t == null) continue;
                    typeCount++;

                    string baseName = (t.BaseType != null) ? t.BaseType.FullName : "-";
                    sb.AppendLine();
                    sb.AppendLine("=== TYPE: " + t.FullName + "    <base: " + baseName + ">");
                    if (typeof(MonoBehaviour).IsAssignableFrom(t)) sb.AppendLine("    [MonoBehaviour]");
                    else if (typeof(ScriptableObject).IsAssignableFrom(t)) sb.AppendLine("    [ScriptableObject]");

                    const BindingFlags BF = BindingFlags.Public | BindingFlags.NonPublic
                        | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

                    FieldInfo[] fs = t.GetFields(BF);
                    for (int i = 0; i < fs.Length; i++)
                    {
                        sb.AppendLine("    FIELD  " + fs[i].FieldType.Name + "  " + fs[i].Name);
                    }

                    PropertyInfo[] ps = t.GetProperties(BF);
                    for (int i = 0; i < ps.Length; i++)
                    {
                        sb.AppendLine("    PROP   " + ps[i].PropertyType.Name + "  " + ps[i].Name);
                    }

                    MethodInfo[] ms = t.GetMethods(BF);
                    for (int i = 0; i < ms.Length; i++)
                    {
                        MethodInfo m = ms[i];
                        StringBuilder pp = new StringBuilder();
                        ParameterInfo[] pars = m.GetParameters();
                        for (int j = 0; j < pars.Length; j++)
                        {
                            if (j > 0) pp.Append(", ");
                            pp.Append(pars[j].ParameterType.Name);
                        }
                        sb.AppendLine("    METHOD " + m.ReturnType.Name + "  " + m.Name + "(" + pp.ToString() + ")");
                    }
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(OutPath));
            File.WriteAllText(OutPath, sb.ToString(), Encoding.UTF8);
            Debug.Log("[Dump] OK -> " + OutPath + "  types=" + typeCount + "  chars=" + sb.Length);
        }
    }
}
