// ════════════════════════════════════════════════════════════════════════════
//  pcl-patcher  |  PCL2 下载限制解除补丁
// ────────────────────────────────────────────────────────────────────────────
//  作用域   PCL 2.99.0.0 / 2.99.0.1
//  依赖     dnlib 4.5.0（以托管资源嵌入，运行时由 AssemblyResolve 取出）
//  原理     VB 的 Const 在编译期内联，受影响方法体被折叠或裁掉，需按 IL 特征
//           定位后用 dnlib 重建
//  输出标签 [进度] [跳过] [错误] [建议] [等待]
// ════════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

static class Patcher
{
    // [状态] 当前加载的目标模块，全部补丁方法共享
    static ModuleDefMD mod;

    // [状态] 已跳过项计数，用于收尾汇总
    static int skipCount;

    // ────────────────────────────────────────────────────────────────────────
    //  入口：统一异常出口 + 交互暂停控制
    // ────────────────────────────────────────────────────────────────────────
    static void Main(string[] args)
    {
        // [说明] dnlib 以资源形式嵌入，此处挂解析器，发布时无需附带 dll
        AppDomain.CurrentDomain.AssemblyResolve += ResolveEmbedded;

        bool pause = false;
        int code = 0;
        try
        {
            code = Dispatch(args, out pause);
        }
        catch (Exception e)
        {
            Console.WriteLine();
            Console.WriteLine("[错误] 补丁未完成，已中止");
            Console.WriteLine();
            Console.WriteLine("       " + e.Message);
            Console.WriteLine();
            Console.WriteLine("[建议] 若无法判断原因，改用官方原版 exe 重试");
            code = 1;
            pause = true;
        }

        // [说明] pause 由各分支决定：需要用户看结果的场景为 true
        if (pause)
        {
            Console.WriteLine();
            Console.WriteLine("[等待] 按任意键关闭窗口…");
            try { Console.ReadKey(true); } catch { }
        }
        Environment.Exit(code);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  参数分派
    //  出口码  0 = 成功  2 = 用法错误  1 = 运行异常
    // ────────────────────────────────────────────────────────────────────────
    [MethodImpl(MethodImplOptions.NoInlining)]
    static int Dispatch(string[] args, out bool pause)
    {
        // [默认] 除成功路径外均需暂停，便于查看输出
        pause = true;

        // [分支] 0 参数：双击运行
        if (args.Length == 0)
        {
            ShowGuide();
            return 2;
        }

        // [分支] 1 参数：拖放
        if (args.Length == 1)
        {
            string one = Clean(args[0]);

            if (Directory.Exists(one))
            {
                Console.WriteLine("[错误] 目标是文件夹，不是 exe");
                Console.WriteLine("[提示] 需要处理的是「Plain Craft Launcher 2.exe」本身");
                Console.WriteLine();
                ShowGuide();
                return 2;
            }

            if (!File.Exists(one))
            {
                Console.WriteLine("[错误] 文件不存在");
                Console.WriteLine("       " + one);
                Console.WriteLine();
                ShowGuide();
                return 2;
            }

            RunDragDrop(one);
            return 0;
        }

        // [分支] 2 参数：命令行
        if (args.Length == 2)
        {
            string src = Clean(args[0]);
            string dst = Clean(args[1]);

            if (!File.Exists(src))
            {
                Console.WriteLine("[错误] 输入文件不存在");
                Console.WriteLine("       " + src);
                Console.WriteLine();
                ShowGuide();
                return 2;
            }

            if (File.Exists(dst))
                Console.WriteLine("[提示] 输出文件已存在，将被覆盖：" + dst);

            pause = false;
            Run(src, dst);
            return 0;
        }

        // [分支] 3 参数及以上：参数数量错误
        Console.WriteLine("[错误] 参数数量超出预期，收到 " + args.Length + " 个");
        for (int i = 0; i < args.Length; i++)
            Console.WriteLine("       [" + i + "] " + args[i]);
        Console.WriteLine();
        Console.WriteLine("[提示] 若一次拖入多个文件，第 2 个会被当作输出路径并覆盖");
        Console.WriteLine("[建议] 改为逐个处理");
        Console.WriteLine();
        ShowGuide();
        return 2;
    }

    // ────────────────────────────────────────────────────────────────────────
    //  用法说明
    // ────────────────────────────────────────────────────────────────────────
    static void ShowGuide()
    {
        Console.WriteLine("────────────────────────────────────────────");
        Console.WriteLine("  PCL2 解锁补丁");
        Console.WriteLine("────────────────────────────────────────────");
        Console.WriteLine();
        Console.WriteLine("[说明] 为 PCL2 原版 exe 打补丁，恢复 MC 下载与整合包安装");
        Console.WriteLine();
        Console.WriteLine("[用法一] 拖放");
        Console.WriteLine("         将「Plain Craft Launcher 2.exe」拖到本程序上");
        Console.WriteLine("         在同一目录生成「… Fix.exe」");
        Console.WriteLine();
        Console.WriteLine("[用法二] 命令行");
        Console.WriteLine("         patch.exe <原版exe> <输出exe>");
    }

    // ────────────────────────────────────────────────────────────────────────
    //  路径规范化
    //  [说明] 拖放时资源管理器可能附带引号，需剥离
    // ────────────────────────────────────────────────────────────────────────
    static string Clean(string s)
    {
        return s.Trim().Trim('"');
    }

    // ────────────────────────────────────────────────────────────────────────
    //  拖放入口
    //  输出命名  原文件名 + " Fix" + 原扩展名
    // ────────────────────────────────────────────────────────────────────────
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void RunDragDrop(string src)
    {
        string full = Path.GetFullPath(src);
        string dir = Path.GetDirectoryName(full);
        string name = Path.GetFileNameWithoutExtension(full);
        string ext = Path.GetExtension(full);

        // [兜底] 无扩展名时按 exe 处理
        if (string.IsNullOrEmpty(ext)) ext = ".exe";

        string dst = Path.Combine(dir, name + " Fix" + ext);
        Run(full, dst);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  内嵌程序集解析
    //  资源名  dnlib.dll
    //  范围    仅响应 dnlib 请求，其余返回 null 交回默认解析
    // ────────────────────────────────────────────────────────────────────────
    [MethodImpl(MethodImplOptions.NoInlining)]
    static Assembly ResolveEmbedded(object sender, ResolveEventArgs e)
    {
        string simpleName;
        try { simpleName = new AssemblyName(e.Name).Name; }
        catch { return null; }

        if (!string.Equals(simpleName, "dnlib", StringComparison.OrdinalIgnoreCase))
            return null;

        using (Stream s = typeof(Patcher).Assembly.GetManifestResourceStream("dnlib.dll"))
        {
            if (s == null) return null;

            // [说明] 手动分块读取，避免单次 Read 未读满
            byte[] buf = new byte[s.Length];
            int off = 0;
            while (off < buf.Length)
            {
                int r = s.Read(buf, off, buf.Length - off);
                if (r <= 0) break;
                off += r;
            }
            return Assembly.Load(buf);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    //  主流程
    //  顺序  校验路径 → 加载模块 → 依次执行 6 处补丁 → 写盘
    // ────────────────────────────────────────────────────────────────────────
    static void Run(string src, string dst)
    {
        // [校验] 输入输出同路径会导致自我覆盖
        if (string.Equals(Path.GetFullPath(src), Path.GetFullPath(dst), StringComparison.OrdinalIgnoreCase))
            throw new Exception("输入与输出为同一文件，请改用其他输出路径。");

        try
        {
            mod = ModuleDefMD.Load(src);
        }
        catch (BadImageFormatException)
        {
            // [校验] 非托管 PE 或非 .NET 程序
            throw new Exception("目标不是 .NET 程序。\n需要处理的是「Plain Craft Launcher 2.exe」本身。");
        }

        Console.WriteLine();
        Console.WriteLine("[信息] 源文件  " + Path.GetFullPath(src));
        Console.WriteLine("[信息] 程序集  " + mod.Assembly.FullName);
        Console.WriteLine("[信息] 输出到  " + Path.GetFullPath(dst));
        Console.WriteLine();

        PatchRestrictConstant();
        PatchLoaderInit();
        PatchVisibility();
        PatchModpackInstall();
        PatchModpackInstallNoArg();
        PatchLeftNav();

        mod.Write(dst);

        Console.WriteLine();
        Console.WriteLine("[完成] 已写出 " + Path.GetFullPath(dst));
        if (skipCount > 0)
            Console.WriteLine("[提示] " + skipCount + " 处结构不匹配已跳过，该版本可能不含对应功能，其余补丁不受影响");
    }

    // ────────────────────────────────────────────────────────────────────────
    //  跳过记录
    // ────────────────────────────────────────────────────────────────────────
    static void Skip(string why)
    {
        skipCount++;
        Console.WriteLine("[跳过] " + why);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  类型查找
    //  required = true  缺失即中止
    //  required = false 缺失则记录跳过
    // ────────────────────────────────────────────────────────────────────────
    static TypeDef FindType(string name, bool required)
    {
        TypeDef t = mod.Find(name, false);
        if (t != null) return t;

        if (required)
            throw new Exception("类型缺失：" + name + "。目标可能不是 PCL2，或版本差异过大。");

        Skip("类型缺失：" + name);
        return null;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  补丁 1/6  下载限制常量
    //  目标      PCL.ModSecret.RestrictMcDownload
    //  依据      VB 将 Const 内联，全库 0 处指令引用，仅元数据保留原值
    //  操作      元数据 Constant 值 True → False
    //  备注      象征性修改，实际生效依赖补丁 2 与补丁 4
    // ════════════════════════════════════════════════════════════════════════
    static void PatchRestrictConstant()
    {
        TypeDef modSecret = FindType("PCL.ModSecret", true);

        FieldDef restrict = modSecret.FindField("RestrictMcDownload");
        if (restrict == null)
            throw new Exception("字段缺失：ModSecret.RestrictMcDownload。");
        if (restrict.Constant == null)
            throw new Exception("RestrictMcDownload 非常量，结构不匹配。");

        restrict.Constant.Value = false;
        Console.WriteLine("[进度] 1/6  下载限制常量 → False");
    }

    // ════════════════════════════════════════════════════════════════════════
    //  补丁 2/6  MC 版本列表加载
    //  目标      PCL.PageDownloadInstall.LoaderInit
    //  依据      折叠后此处构造的是「关闭下载」提示用 LoaderTask
    //  操作      ldstr 位置改为 ldsfld ModDownload.DlClientListLoader，
    //            其后 12 条指令置为 nop
    //  判据      ldstr 之后第 12 条为 newobj，且构造类型名含 LoaderTask
    // ════════════════════════════════════════════════════════════════════════
    static void PatchLoaderInit()
    {
        TypeDef t = FindType("PCL.PageDownloadInstall", true);

        MethodDef m = t.FindMethod("LoaderInit");
        if (m == null || !m.HasBody)
            throw new Exception("方法缺失：PageDownloadInstall.LoaderInit。");

        IList<Instruction> ins = m.Body.Instructions;

        // [定位] 按 ldstr → newobj(LoaderTask) 的固定间隔匹配
        int k = -1;
        for (int i = 0; i + 12 < ins.Count; i++)
        {
            if (ins[i].OpCode.Code != Code.Ldstr) continue;
            if (ins[i + 12].OpCode.Code != Code.Newobj) continue;

            IMethod ctor = ins[i + 12].Operand as IMethod;
            if (ctor == null || ctor.DeclaringType == null) continue;
            if (ctor.DeclaringType.Name.String.IndexOf("LoaderTask", StringComparison.Ordinal) < 0) continue;

            k = i;
            break;
        }

        if (k < 0)
            throw new Exception("特征未匹配：LoaderInit 中未找到假 Loader。结构可能变化，或已打过补丁。");

        TypeDef modDownload = FindType("PCL.ModDownload", true);
        FieldDef dl = modDownload.FindField("DlClientListLoader");
        if (dl == null)
            throw new Exception("字段缺失：ModDownload.DlClientListLoader。");

        // [改写] 起始指令换成真实 Loader 字段加载
        ins[k].OpCode = OpCodes.Ldsfld;
        ins[k].Operand = dl;

        // [改写] 后续 12 条原有构造指令全部作废
        for (int j = 1; j <= 12; j++)
        {
            ins[k + j].OpCode = OpCodes.Nop;
            ins[k + j].Operand = null;
        }

        Console.WriteLine("[进度] 2/6  MC 版本列表加载 → 真实 Loader");
    }

    // ════════════════════════════════════════════════════════════════════════
    //  补丁 3/6  整合包卡片可见性
    //  目标      PCL.PageDownloadModify.LoadInstanceFinished
    //  依据      卡片 Visibility 被硬编码为 Collapsed（ldc.i4.2）
    //  操作      该常量改为 ldc.i4.0（Visible）
    //  判据      get_CardMinecraft → ldc.i4.2 → set_Visibility 三元序列
    // ════════════════════════════════════════════════════════════════════════
    static void PatchVisibility()
    {
        TypeDef t = FindType("PCL.PageDownloadModify", true);

        int n = 0;
        foreach (MethodDef m in t.Methods)
        {
            if (!m.HasBody) continue;

            IList<Instruction> ins = m.Body.Instructions;
            for (int i = 0; i + 2 < ins.Count; i++)
            {
                IMethod get = ins[i].Operand as IMethod;
                if (get == null || get.Name.String != "get_CardMinecraft") continue;
                if (ins[i + 1].OpCode.Code != Code.Ldc_I4_2) continue;

                IMethod set = ins[i + 2].Operand as IMethod;
                if (set == null || set.Name.String != "set_Visibility") continue;

                ins[i + 1].OpCode = OpCodes.Ldc_I4_0;
                ins[i + 1].Operand = null;
                n++;
            }
        }

        if (n == 0)
            throw new Exception("特征未匹配：未找到整合包卡片的可见性赋值。");

        Console.WriteLine("[进度] 3/6  整合包卡片可见性 → Visible（" + n + " 处）");
    }

    // ════════════════════════════════════════════════════════════════════════
    //  补丁 4/6  整合包类型分发（三参数重载）
    //  目标      PCL.ModModpack.ModpackInstall(String, String, String)
    //  依据      方法体被折叠为「提示 + 抛异常」，类型判定 switch 被裁掉
    //  操作      在原异常分支处插入对 ModpackInstallDispatch 的调用，
    //            由新建方法按编号分发到 7 个 InstallPack* 实现
    //  判据      末尾存在 ldstr(含 "qwq") → newobj(Exception) → throw 序列
    //  风险      finally 的 HandlerEnd 为 null 表示「延伸至方法尾」，
    //            末尾追加指令后须显式补结束标记，否则 leave 目标落入保护区
    // ════════════════════════════════════════════════════════════════════════
    static void PatchModpackInstall()
    {
        TypeDef t = FindType("PCL.ModModpack", true);

        // [定位] 三参数重载指令数显著大于无参重载
        MethodDef target = null;
        foreach (MethodDef m in t.Methods)
        {
            if (m.Name.String == "ModpackInstall" && m.HasBody && m.Body.Instructions.Count > 100)
            {
                target = m;
                break;
            }
        }

        if (target == null)
            throw new Exception("方法缺失：三参数 ModpackInstall。");

        CilBody body = target.Body;
        IList<Instruction> ins = body.Instructions;

        // [定位] 折叠后残留的限制分支
        int ti = -1;
        for (int i = 0; i < ins.Count; i++)
        {
            if (ins[i].OpCode.Code != Code.Throw) continue;
            if (i < 2) continue;
            if (ins[i - 1].OpCode.Code != Code.Newobj) continue;
            if (ins[i - 2].OpCode.Code != Code.Ldstr) continue;

            string s = ins[i - 2].Operand as string;
            if (s == null || s.IndexOf("qwq", StringComparison.Ordinal) < 0) continue;

            ti = i;
            break;
        }

        if (ti < 0)
            throw new Exception("特征未匹配：未找到「不再提供 MC 下载」异常分支。结构可能变化，或已打过补丁。");

        Instruction ldstrIns = ins[ti - 2];

        // [说明] 局部变量沿用原方法布局，避免重建局部变量表
        Local v0 = body.Variables[0];   // 返回值
        Local v1 = body.Variables[1];   // ZipArchive
        Local v2 = body.Variables[2];   // overrides 前缀
        Local v4 = body.Variables[4];   // 类型编号

        MethodDef dispatch = CreateDispatch(t, target);

        // [说明] leave 目标置于方法末尾，需与下方 endMarker 位置一致
        Instruction lout = Instruction.Create(OpCodes.Ldloc, v0);

        // [改写] 原 ldstr 位置改作编号加载
        ldstrIns.OpCode = OpCodes.Ldloc;
        ldstrIns.Operand = v4;

        // [插入] 组装分发调用：编号 + 文件 + ZipArchive + 两个字符串参数
        List<Instruction> seq = new List<Instruction>();
        seq.Add(Instruction.Create(OpCodes.Ldarg_0));
        seq.Add(Instruction.Create(OpCodes.Ldloc, v1));
        seq.Add(Instruction.Create(OpCodes.Ldloc, v2));
        seq.Add(Instruction.Create(OpCodes.Ldarg_1));
        seq.Add(Instruction.Create(OpCodes.Ldarg_2));
        seq.Add(Instruction.Create(OpCodes.Call, dispatch));
        seq.Add(Instruction.Create(OpCodes.Stloc, v0));
        seq.Add(Instruction.Create(OpCodes.Leave, lout));

        // [改写] 用分发序列替换原来的 newobj + throw
        int start = ins.IndexOf(ldstrIns);
        ins.RemoveAt(start + 2);
        ins.RemoveAt(start + 1);
        for (int j = 0; j < seq.Count; j++)
            ins.Insert(start + 1 + j, seq[j]);

        // [修正] finally 结束标记
        // HandlerEnd 为 null 时语义是「延伸至方法尾」，追加指令会把新代码
        // 卷进保护区，导致 leave 目标落在 try 内部 → InvalidProgramException。
        Instruction endMarker = Instruction.Create(OpCodes.Nop);
        ins.Add(endMarker);
        ins.Add(lout);
        ins.Add(Instruction.Create(OpCodes.Ret));

        foreach (ExceptionHandler eh in body.ExceptionHandlers)
        {
            if (eh.HandlerType == ExceptionHandlerType.Finally && eh.HandlerEnd == null)
                eh.HandlerEnd = endMarker;
        }

        Console.WriteLine("[进度] 4/6  整合包类型分发 → 已重建");
    }

    // ────────────────────────────────────────────────────────────────────────
    //  生成分发方法 ModpackInstallDispatch
    //  签名  (Int32, String, ZipArchive, String, String, String) → LoaderCombo
    //  映射  0 CurseForge | 1 HMCL | 2 MMC | 3 MCBBS | 4 Modrinth | 9 LauncherPack
    //        其余 → Compress
    //  备注  ZipArchive 类型取自原方法局部变量，避免自行拼装签名
    // ────────────────────────────────────────────────────────────────────────
    static MethodDef CreateDispatch(TypeDef t, MethodDef orig)
    {
        TypeSig zip = orig.Body.Variables[1].Type;

        MethodSig sig = MethodSig.CreateStatic(orig.ReturnType, new TypeSig[] {
            mod.CorLibTypes.Int32, mod.CorLibTypes.String, zip,
            mod.CorLibTypes.String, mod.CorLibTypes.String, mod.CorLibTypes.String
        });

        MethodDef md = new MethodDefUser("ModpackInstallDispatch", sig,
            dnlib.DotNet.MethodImplAttributes.IL | dnlib.DotNet.MethodImplAttributes.Managed,
            dnlib.DotNet.MethodAttributes.Private | dnlib.DotNet.MethodAttributes.Static | dnlib.DotNet.MethodAttributes.HideBySig);

        t.Methods.Add(md);

        CilBody b = new CilBody();
        b.InitLocals = false;
        md.Body = b;

        // [说明] 参数定义数量与签名需一致，否则写入时元数据不匹配
        if (md.ParamDefs.Count != 6)
        {
            md.ParamDefs.Clear();
            for (int i = 0; i < 6; i++)
                md.ParamDefs.Add(new ParamDefUser("arg" + i, (ushort)(i + 1)));
        }

        MethodDef mCurse = FindMethod(t, "InstallPackCurseForge");
        MethodDef mModrinth = FindMethod(t, "InstallPackModrinth");
        MethodDef mHmcl = FindMethod(t, "InstallPackHMCL");
        MethodDef mMmc = FindMethod(t, "InstallPackMMC");
        MethodDef mMcbbs = FindMethod(t, "InstallPackMCBBS");
        MethodDef mLauncher = FindMethod(t, "InstallPackLauncherPack");
        MethodDef mCompress = FindMethod(t, "InstallPackCompress");

        Instruction c0 = Instruction.Create(OpCodes.Nop);
        Instruction c1 = Instruction.Create(OpCodes.Nop);
        Instruction c2 = Instruction.Create(OpCodes.Nop);
        Instruction c3 = Instruction.Create(OpCodes.Nop);
        Instruction c4 = Instruction.Create(OpCodes.Nop);
        Instruction c9 = Instruction.Create(OpCodes.Nop);
        Instruction cElse = Instruction.Create(OpCodes.Nop);

        // [结构] switch 覆盖 0–4，9 单独比较，其余走默认分支
        b.Instructions.Add(Instruction.Create(OpCodes.Ldarg, md.Parameters[0]));
        b.Instructions.Add(Instruction.Create(OpCodes.Switch, new List<Instruction> { c0, c1, c2, c3, c4 }));
        b.Instructions.Add(Instruction.Create(OpCodes.Ldarg, md.Parameters[0]));
        b.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4, 9));
        b.Instructions.Add(Instruction.Create(OpCodes.Beq, c9));
        b.Instructions.Add(Instruction.Create(OpCodes.Br, cElse));

        AddCase(b, md, c0, mCurse, new int[] { 1, 2, 3, 4, 5 });
        AddCase(b, md, c1, mHmcl, new int[] { 1, 2, 3 });
        AddCase(b, md, c2, mMmc, new int[] { 1, 2, 3 });
        AddCase(b, md, c3, mMcbbs, new int[] { 1, 2, 3, 4 });
        AddCase(b, md, c4, mModrinth, new int[] { 1, 2, 3, 4, 5 });
        AddCase(b, md, c9, mLauncher, new int[] { 1, 2, 3 });
        AddCase(b, md, cElse, mCompress, new int[] { 1, 2 });

        return md;
    }

    // ────────────────────────────────────────────────────────────────────────
    //  生成单个分支：标签 → 按索引取参数 → 调用 → ret
    //  args 为形参序号，按目标方法签名顺序排列
    // ────────────────────────────────────────────────────────────────────────
    static void AddCase(CilBody b, MethodDef md, Instruction label, MethodDef target, int[] args)
    {
        b.Instructions.Add(label);

        for (int i = 0; i < args.Length; i++)
            b.Instructions.Add(Instruction.Create(OpCodes.Ldarg, md.Parameters[args[i]]));

        b.Instructions.Add(Instruction.Create(OpCodes.Call, target));
        b.Instructions.Add(Instruction.Create(OpCodes.Ret));
    }

    // ════════════════════════════════════════════════════════════════════════
    //  补丁 5/6  整合包文件选择入口（无参重载）
    //  目标      PCL.ModModpack.ModpackInstall()
    //  依据      方法体被清空为「提示 + ret」
    //  操作      重建文件选择对话框调用链，选中后转调三参数重载
    //  备注      SelectFile 的参数类型从 ModJava.ManuallyImportJava 借用，
    //            避免自行构造 OneOf / Nullable 泛型签名
    // ════════════════════════════════════════════════════════════════════════
    static void PatchModpackInstallNoArg()
    {
        TypeDef t = FindType("PCL.ModModpack", true);

        MethodDef m0 = null;
        MethodDef m3 = null;
        foreach (MethodDef m in t.Methods)
        {
            if (m.Name.String != "ModpackInstall") continue;
            if (m.MethodSig.Params.Count == 0) m0 = m;
            else if (m.MethodSig.Params.Count == 3) m3 = m;
        }

        if (m0 == null || m3 == null)
            throw new Exception("方法缺失：ModpackInstall 重载不完整。");

        // [借用] 从已有调用点取 SelectFile 及其相邻构造引用
        TypeDef java = FindType("PCL.ModJava", true);
        MethodDef jm = java.FindMethod("ManuallyImportJava");
        if (jm == null || !jm.HasBody)
            throw new Exception("方法缺失：ModJava.ManuallyImportJava，无法借用 SelectFile 签名。");

        IList<Instruction> jins = jm.Body.Instructions;

        IMethod selectFile = null, oneOf = null, nullableCtor = null, firstOrDefault = null;
        for (int i = 3; i + 2 < jins.Count; i++)
        {
            IMethod im = jins[i].Operand as IMethod;
            if (im == null || im.Name.String != "SelectFile") continue;

            selectFile = im;
            oneOf = jins[i - 2].Operand as IMethod;       // OneOf.op_Implicit
            nullableCtor = jins[i - 1].Operand as IMethod; // Nullable`1::.ctor
            firstOrDefault = jins[i + 1].Operand as IMethod;
            break;
        }

        if (selectFile == null || oneOf == null || nullableCtor == null || firstOrDefault == null)
            throw new Exception("特征未匹配：SelectFile 调用链无法识别，结构可能变化。");

        IMethod isNullOrEmpty = FindCall("IsNullOrEmpty");
        if (isNullOrEmpty == null)
            throw new Exception("引用缺失：全库未找到 IsNullOrEmpty。");

        // [重建] 清空原方法体后按目标语义重写
        CilBody b = m0.Body;
        b.Instructions.Clear();
        b.ExceptionHandlers.Clear();
        b.Variables.Clear();

        Local vFile = new Local(mod.CorLibTypes.String);
        b.Variables.Add(vFile);
        b.InitLocals = true;

        Instruction ret = Instruction.Create(OpCodes.Ret);

        // 对话框标题
        b.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "选择整合包文件"));
        b.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_0));
        b.Instructions.Add(Instruction.Create(OpCodes.Ldnull));

        // [格式] 过滤器为「扩展名;扩展名|显示名」，竖线右侧为显示文本
        b.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "rar;zip;mrpack|整合包文件"));

        b.Instructions.Add(Instruction.Create(OpCodes.Call, oneOf));
        b.Instructions.Add(Instruction.Create(OpCodes.Newobj, nullableCtor));
        b.Instructions.Add(Instruction.Create(OpCodes.Call, selectFile));
        b.Instructions.Add(Instruction.Create(OpCodes.Call, firstOrDefault));
        b.Instructions.Add(Instruction.Create(OpCodes.Stloc, vFile));

        // [分支] 用户取消则直接返回
        b.Instructions.Add(Instruction.Create(OpCodes.Ldloc, vFile));
        b.Instructions.Add(Instruction.Create(OpCodes.Call, isNullOrEmpty));
        b.Instructions.Add(Instruction.Create(OpCodes.Brtrue, ret));

        // [调用] 转交三参数重载，返回值丢弃
        b.Instructions.Add(Instruction.Create(OpCodes.Ldloc, vFile));
        b.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
        b.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
        b.Instructions.Add(Instruction.Create(OpCodes.Call, m3));
        b.Instructions.Add(Instruction.Create(OpCodes.Pop));

        b.Instructions.Add(ret);

        Console.WriteLine("[进度] 5/6  整合包文件选择入口 → 已重建");
    }

    // ════════════════════════════════════════════════════════════════════════
    //  补丁 6/6  下载页侧栏入口
    //  目标      PCL.PageDownloadLeft..ctor
    //  依据      2.99.0.1 在 InitializeComponent 之后追加隐藏逻辑：
    //              ItemInstall.Visibility   → Collapsed
    //              LabResource.Visibility   → Collapsed
    //              PageID                   → 模组
    //              ItemMod.Checked          → true
    //  操作      截断 InitializeComponent 之后的全部指令，补回 ret
    //  判据      截断点之后存在 callvirt set_Visibility，否则视为非目标结构
    //  备注      2.99.0.0 本无该逻辑，命中精简形态时直接跳过
    // ════════════════════════════════════════════════════════════════════════
    static void PatchLeftNav()
    {
        TypeDef t = FindType("PCL.PageDownloadLeft", false);
        if (t == null) return;

        MethodDef ctor = null;
        foreach (MethodDef m in t.Methods)
        {
            if (m.Name.String == ".ctor" && !m.IsStatic && m.HasBody) { ctor = m; break; }
        }

        if (ctor == null)
        {
            Skip("PageDownloadLeft 缺少实例构造函数");
            return;
        }

        CilBody body = ctor.Body;
        IList<Instruction> ins = body.Instructions;

        // [定位] 以 InitializeComponent 调用点为截断基准
        int cut = -1;
        for (int i = 0; i < ins.Count; i++)
        {
            IMethod im = ins[i].Operand as IMethod;
            if (ins[i].OpCode.Code == Code.Call && im != null && im.Name.String == "InitializeComponent")
            {
                cut = i;
                break;
            }
        }

        if (cut < 0)
        {
            Skip("构造函数中未调用 InitializeComponent");
            return;
        }

        // [短路] 调用点之后仅剩 ret，说明已是解锁形态
        if (ins.Count == cut + 2 && ins[cut + 1].OpCode.Code == Code.Ret)
        {
            Console.WriteLine("[进度] 6/6  下载页侧栏 → 已是解锁形态，无需处理");
            return;
        }

        // [防误伤] 必须确认后续确实在设置可见性，否则不视为目标结构
        bool hiding = false;
        for (int i = cut + 1; i < ins.Count; i++)
        {
            IMethod m2 = ins[i].Operand as IMethod;
            if (ins[i].OpCode.Code == Code.Callvirt && m2 != null && m2.Name.String == "set_Visibility")
                hiding = true;
        }

        if (!hiding)
        {
            Skip("构造函数末尾无 set_Visibility，结构不符合预期");
            return;
        }

        // [截断] 移除隐藏逻辑并补回返回指令
        int removed = ins.Count - (cut + 1);
        while (ins.Count > cut + 1)
            ins.RemoveAt(ins.Count - 1);

        ins.Add(Instruction.Create(OpCodes.Ret));

        // [同步] 截断后需重算偏移并保证 MaxStack 足够容纳 set_Visibility 的调用
        body.ExceptionHandlers.Clear();
        body.UpdateInstructionOffsets();
        if (body.MaxStack < 2) body.MaxStack = 2;

        Console.WriteLine("[进度] 6/6  下载页侧栏入口 → 已恢复（移除 " + removed + " 条隐藏指令）");
    }

    // ────────────────────────────────────────────────────────────────────────
    //  按名称取方法，缺失即中止
    // ────────────────────────────────────────────────────────────────────────
    static MethodDef FindMethod(TypeDef t, string name)
    {
        foreach (MethodDef m in t.Methods)
            if (m.Name.String == name) return m;

        throw new Exception("方法缺失：" + t.Name + "." + name + "。");
    }

    // ────────────────────────────────────────────────────────────────────────
    //  全库扫描指定名称的方法引用，取首个命中
    //  [说明] 用于借用已有调用的完整签名，避免自行构造泛型参数
    // ────────────────────────────────────────────────────────────────────────
    static IMethod FindCall(string name)
    {
        foreach (TypeDef t in mod.GetTypes())
        {
            foreach (MethodDef m in t.Methods)
            {
                if (!m.HasBody) continue;

                foreach (Instruction ins in m.Body.Instructions)
                {
                    IMethod im = ins.Operand as IMethod;
                    if (im != null && im.Name.String == name) return im;
                }
            }
        }
        return null;
    }
}
