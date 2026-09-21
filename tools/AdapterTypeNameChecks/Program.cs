using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;

// 命名空间常量与 AfpReflection 里的那份一一对应；对方改布局或适配改查找入口时，
// 两边必须一起更新（源文件：adapters/CombatSolver.ActsFromThePastAdapter/AfpReflection.cs）。
const string MonsterNamespace = "ActsFromThePast";
const string BeyondEnemyNamespace = "ActsFromThePast.Acts.TheBeyond.Enemies";
const string PowerNamespace = "ActsFromThePast.Powers";
const string AfflictionNamespace = "ActsFromThePast.Afflictions";
const string AdapterDirName = "CombatSolver.ActsFromThePastAdapter";

string? assemblyArgument = args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]) ? args[0] : null;
string? sourceArgument = args.Length > 1 && !string.IsNullOrWhiteSpace(args[1]) ? args[1] : null;

string sourceDir = sourceArgument is null ? LocateAdapterSource() : Path.GetFullPath(sourceArgument);
string assemblyPath = assemblyArgument is null
    ? LocateActsFromThePastAssembly() ?? throw new FileNotFoundException(
        "找不到已安装的 ActsFromThePast.dll。请把程序集路径作为第一个参数传入，或用 COMBATSOLVER_AFTP_ASSEMBLY 指定。\n"
        + string.Join("\n", AssemblyCandidates().Select(candidate => "  " + candidate)))
    : Path.GetFullPath(assemblyArgument);

using ActsFromThePastMetadata metadata = ActsFromThePastMetadata.Read(assemblyPath);

List<string> failures = [];
int monsterNames = 0;
int powerNames = 0;
int afflictionNames = 0;
int exactNames = 0;
int memberTypeNames = 0;
int overrideChecks = 0;
int constChecks = 0;
int memberChecks = 0;

foreach (string file in Directory.EnumerateFiles(sourceDir, "*.cs", SearchOption.TopDirectoryOnly)
             .OrderBy(path => path, StringComparer.Ordinal))
{
    // AfpReflection.cs 是查找实现本身，不参与「调用点」扫描。
    if (string.Equals(Path.GetFileName(file), "AfpReflection.cs", StringComparison.Ordinal))
        continue;

    string text = File.ReadAllText(file);
    string name = Path.GetFileName(file);

    foreach (Match match in Matches(text, @"AfpReflection\.RequireMonster(?:Type)?\(\s*""([A-Za-z0-9_]+)""\s*\)"))
    {
        monsterNames++;
        RequireUniqueAcross(monsterNamespaces: true, match.Groups[1].Value, name);
    }

    foreach (Match match in Matches(text, @"AfpReflection\.RequirePowerType\(\s*""([A-Za-z0-9_]+)""\s*\)"))
    {
        powerNames++;
        RequireInNamespace(match.Groups[1].Value, PowerNamespace, name);
    }

    foreach (Match match in Matches(text, @"AfpReflection\.RequireAfflictionType\(\s*""([A-Za-z0-9_]+)""\s*\)"))
    {
        afflictionNames++;
        RequireInNamespace(match.Groups[1].Value, AfflictionNamespace, name);
    }

    foreach (Match match in Matches(text, @"AfpReflection\.RequireType\(\s*""([A-Za-z0-9_.]+)""\s*\)"))
    {
        exactNames++;
        RequireExact(match.Groups[1].Value, name);
    }

    // RequireType(常量名)：常量声明在同一文件里。
    foreach (Match match in Matches(text, @"AfpReflection\.RequireType\(\s*([A-Za-z0-9_]+)\s*\)"))
    {
        string constant = match.Groups[1].Value;
        Match declaration = Regex.Match(
            text,
            @"const\s+string\s+" + Regex.Escape(constant) + @"\s*=\s*""([A-Za-z0-9_.]+)""");
        if (!declaration.Success)
        {
            failures.Add($"{name}: RequireType({constant}) 找不到对应的 const string 声明，无法核对。");
            continue;
        }
        exactNames++;
        RequireExact(declaration.Groups[1].Value, $"{name} ({constant})");
    }

    // RequireOverride(类型, 方法, 参数个数)：按 AfpReflection 的口径核对「确实重写了」。
    foreach (Match match in Matches(
                 text,
                 @"AfpReflection\.RequireOverride\(\s*""([A-Za-z0-9_.]+)""\s*,\s*""([A-Za-z0-9_]+)""\s*,\s*(\d+)\s*\)"))
    {
        overrideChecks++;
        string typeName = match.Groups[1].Value;
        string method = match.Groups[2].Value;
        int parameterCount = int.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
        string? fullName = ResolveDeclaredType(typeName, name);
        if (fullName is null)
            continue;
        if (!metadata.HasOverride(fullName, method, parameterCount))
        {
            failures.Add(
                $"{name}: {fullName}.{method}（{parameterCount} 参）不再是重写，往昔之章版本可能已变动。");
        }
    }

    // RequireConst(类型, 常量, 期望值)：核对常量仍在且取值与适配钉死的一致。
    foreach (Match match in Matches(
                 text,
                 @"AfpReflection\.RequireConst\(\s*""([A-Za-z0-9_.]+)""\s*,\s*""([A-Za-z0-9_]+)""\s*,\s*(-?\d+)\s*\)"))
    {
        constChecks++;
        string typeName = match.Groups[1].Value;
        string field = match.Groups[2].Value;
        int expected = int.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
        string? fullName = ResolveDeclaredType(typeName, name);
        if (fullName is null)
            continue;
        int? actual = metadata.ConstValue(fullName, field);
        if (actual is null)
        {
            failures.Add($"{name}: {fullName}.{field} 不再是一个 int 常量（缺失或改了类型）。");
            continue;
        }
        if (actual.Value != expected)
        {
            failures.Add(
                $"{name}: {fullName}.{field} = {actual.Value}，适配层钉死的是 {expected}；"
                + "往昔之章改了数值，适配需要重新核对。");
        }
    }

    // 根捕获/指纹用的怪物成员名单：成员必须真的存在（字段或属性），否则根捕获时才炸。
    foreach (string api in new[] { "RegisterMonsterStateMembers", "RegisterStaticIntMembers" })
    {
        foreach (Match match in Matches(
                     text,
                     @"ThirdPartyAdapterRegistry\." + api + @"\(\s*""([A-Za-z0-9_]+)""([^)]*)\)"))
        {
            string monsterName = match.Groups[1].Value;
            string? fullName = ResolveMonster(monsterName, name);
            if (fullName is null)
                continue;
            foreach (Match member in Matches(match.Groups[2].Value, @"""([A-Za-z0-9_]+)"""))
            {
                memberChecks++;
                string memberName = member.Groups[1].Value;
                if (!metadata.HasFieldOrProperty(fullName, memberName))
                {
                    failures.Add(
                        $"{name}: {api} 里的 {fullName}.{memberName} 不存在（不是字段也不是属性），"
                        + "根捕获会拿不到这个成员。");
                }
            }
        }
    }
}

if (failures.Count > 0)
{
    Console.Error.WriteLine($"ADAPTER_TYPE_NAMES_FAILED count={failures.Count} assembly={assemblyPath}");
    foreach (string failure in failures)
        Console.Error.WriteLine("  " + failure);
    return 1;
}

Console.WriteLine(
    $"ADAPTER_TYPE_NAMES_OK assembly={assemblyPath} source={sourceDir} "
    + $"monsters={monsterNames} powers={powerNames} afflictions={afflictionNames} "
    + $"exact={exactNames} memberTypes={memberTypeNames} overrides={overrideChecks} consts={constChecks} "
    + $"stateMembers={memberChecks} declaredTypes={metadata.TypeCount}");
return 0;

// === 核对 ===

void RequireExact(string fullName, string where)
{
    if (!metadata.HasType(fullName))
        failures.Add($"{where}: {fullName} 不存在（已试全名匹配）。");
}

void RequireInNamespace(string typeName, string @namespace, string where)
{
    string fullName = $"{@namespace}.{typeName}";
    if (metadata.HasType(fullName))
        return;
    failures.Add(DescribeMiss(typeName, where, $"不在 {@namespace}"));
}

void RequireUniqueAcross(bool monsterNamespaces, string typeName, string where)
{
    List<string> namespaces = [MonsterNamespace, PowerNamespace, AfflictionNamespace];
    if (monsterNamespaces)
        namespaces.Insert(1, BeyondEnemyNamespace);

    string[] hits = namespaces
        .Select(candidate => $"{candidate}.{typeName}")
        .Where(metadata.HasType)
        .ToArray();
    if (hits.Length == 1)
        return;
    if (hits.Length == 0)
    {
        failures.Add(DescribeMiss(
            typeName, where, $"不在适配登记的命名空间（已试 {string.Join("、", namespaces)}）"));
        return;
    }
    failures.Add($"{where}: {typeName} 同时命中 {string.Join("、", hits)}，适配无法判断该用哪一个。");
}

string DescribeMiss(string typeName, string where, string reason)
{
    string found = metadata.Candidates(typeName);
    return found.Length == 0
        ? $"{where}: {typeName} 不存在，往昔之章版本可能已变动。"
        : $"{where}: {typeName} {reason}；实际在 {found}。";
}

// 只用于 RequireOverride／RequireConst：与 AfpReflection.ResolveDeclaredType 同一口径。
string? ResolveDeclaredType(string typeName, string where)
{
    if (typeName.Contains('.', StringComparison.Ordinal))
    {
        RequireExact(typeName, where);
        return metadata.HasType(typeName) ? typeName : null;
    }

    memberTypeNames++;
    string[] candidates =
    [
        $"{MonsterNamespace}.{typeName}",
        $"{BeyondEnemyNamespace}.{typeName}",
        $"{PowerNamespace}.{typeName}",
        $"{AfflictionNamespace}.{typeName}",
    ];
    string[] hits = candidates.Where(metadata.HasType).ToArray();
    if (hits.Length == 1)
        return hits[0];
    failures.Add(hits.Length == 0
        ? $"{where}: {typeName} 不存在（已试 {string.Join("、", candidates)}），往昔之章版本可能已变动。"
        : $"{where}: {typeName} 同时命中 {string.Join("、", hits)}，适配需要改为按用途显式登记。");
    return null;
}

// 怪物成员名单登记只写怪物名：与 AfpReflection.RequireMonster 同一口径。
string? ResolveMonster(string typeName, string where)
{
    string[] candidates = [$"{MonsterNamespace}.{typeName}", $"{BeyondEnemyNamespace}.{typeName}"];
    string[] hits = candidates.Where(metadata.HasType).ToArray();
    if (hits.Length == 1)
        return hits[0];
    failures.Add(hits.Length == 0
        ? $"{where}: 怪物 {typeName} 不存在（已试 {string.Join("、", candidates)}），往昔之章版本可能已变动。"
        : $"{where}: 怪物 {typeName} 同时命中 {string.Join("、", hits)}，适配无法判断该用哪一个。");
    return null;
}

static MatchCollection Matches(string text, string pattern) => Regex.Matches(text, pattern);

static string LocateAdapterSource()
{
    DirectoryInfo? current = new(AppContext.BaseDirectory);
    while (current is not null)
    {
        string candidate = Path.Combine(current.FullName, "adapters", AdapterDirName);
        if (Directory.Exists(candidate))
            return candidate;
        current = current.Parent;
    }
    throw new DirectoryNotFoundException(
        $"从 {AppContext.BaseDirectory} 向上找不到 adapters/{AdapterDirName}；请把适配源码目录作为第二个参数传入。");
}

static string? LocateActsFromThePastAssembly()
{
    string? fromEnvironment = Environment.GetEnvironmentVariable("COMBATSOLVER_AFTP_ASSEMBLY");
    if (!string.IsNullOrWhiteSpace(fromEnvironment) && File.Exists(fromEnvironment))
        return fromEnvironment;

    return AssemblyCandidates()
        .Where(File.Exists)
        .OrderByDescending(File.GetLastWriteTimeUtc)
        .FirstOrDefault();
}

static IEnumerable<string> AssemblyCandidates()
{
    List<string> steamRoots = [];
    foreach (string variable in new[] { "ProgramFiles(x86)", "ProgramFiles", "HOME" })
    {
        string? root = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(root))
            continue;
        steamRoots.Add(Path.Combine(root, "Steam"));
        steamRoots.Add(Path.Combine(root, ".local", "share", "Steam"));
    }
    steamRoots.Add(@"D:\Steam");

    foreach (string steamRoot in steamRoots.Distinct(StringComparer.OrdinalIgnoreCase))
    {
        string steamapps = Path.Combine(steamRoot, "steamapps");
        yield return Path.Combine(
            steamapps, "common", "Slay the Spire 2", "mods", "ActsFromThePast", "ActsFromThePast.dll");
        foreach (string item in WorkshopItems(Path.Combine(steamapps, "workshop", "content", "2868840")))
            yield return Path.Combine(item, "ActsFromThePast.dll");
        foreach (string snapshot in PreCombatSnapshots(Path.Combine(steamapps, "common", "Slay the Spire 2")))
            yield return snapshot;
    }
}

static IEnumerable<string> WorkshopItems(string workshopContent)
{
    if (!Directory.Exists(workshopContent))
        return [];
    return Directory.EnumerateDirectories(workshopContent)
        .Where(item => File.Exists(Path.Combine(item, "ActsFromThePast.json")));
}

static IEnumerable<string> PreCombatSnapshots(string gameRoot)
{
    string precombat = Path.Combine(gameRoot, ".combatsolver-precombat");
    if (!Directory.Exists(precombat))
        return [];
    return Directory.EnumerateDirectories(precombat)
        .SelectMany(process => Directory.Exists(Path.Combine(process, "startup-mods"))
            ? Directory.EnumerateDirectories(Path.Combine(process, "startup-mods"))
                .Where(mod => mod.EndsWith("_ActsFromThePast", StringComparison.Ordinal))
            : [])
        .Select(mod => Path.Combine(mod, "ActsFromThePast.dll"))
        .Where(File.Exists);
}

/// <summary>
/// 只读 PE 元数据的往昔之章索引：类型全名、重写形状与 int 常量。
/// </summary>
/// <remarks>
/// 不加载程序集：往昔之章的类型都继承游戏与 BaseLib 的类型，普通进程里
/// <c>Assembly.GetType</c> 会因为基类型解析不了而返回 null，只有元数据可用。
/// 索引在 <see cref="Dispose"/> 之前一直有效（<see cref="MetadataReader"/> 指向 PE 映像内存）。
/// </remarks>
internal sealed class ActsFromThePastMetadata : IDisposable
{
    private readonly FileStream _stream;
    private readonly PEReader _peReader;
    private readonly MetadataReader _metadata;
    private readonly Dictionary<string, TypeDefinitionHandle> _types;

    private ActsFromThePastMetadata(
        FileStream stream,
        PEReader peReader,
        MetadataReader metadata,
        Dictionary<string, TypeDefinitionHandle> types)
    {
        _stream = stream;
        _peReader = peReader;
        _metadata = metadata;
        _types = types;
    }

    public int TypeCount => _types.Count;

    public static ActsFromThePastMetadata Read(string assemblyPath)
    {
        FileStream stream = File.OpenRead(assemblyPath);
        try
        {
            var peReader = new PEReader(stream);
            MetadataReader metadata = peReader.GetMetadataReader();
            Dictionary<string, TypeDefinitionHandle> types = new(StringComparer.Ordinal);
            foreach (TypeDefinitionHandle handle in metadata.TypeDefinitions)
            {
                TypeDefinition definition = metadata.GetTypeDefinition(handle);
                string @namespace = metadata.GetString(definition.Namespace);
                string name = metadata.GetString(definition.Name);
                types[@namespace.Length == 0 ? name : $"{@namespace}.{name}"] = handle;
            }
            return new ActsFromThePastMetadata(stream, peReader, metadata, types);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _peReader.Dispose();
        _stream.Dispose();
    }

    public bool HasType(string fullName) => _types.ContainsKey(fullName);

    public string Candidates(string simpleName)
        => string.Join(
            "、",
            _types.Keys
                .Where(fullName => fullName.EndsWith("." + simpleName, StringComparison.Ordinal))
                .OrderBy(fullName => fullName, StringComparer.Ordinal));

    /// <summary>按 AfpReflection.RequireOverride 的判据：virtual 且不是 NewSlot（即 override）。</summary>
    public bool HasOverride(string fullName, string methodName, int parameterCount)
    {
        if (!_types.TryGetValue(fullName, out TypeDefinitionHandle handle))
            return false;
        TypeDefinition definition = _metadata.GetTypeDefinition(handle);
        foreach (MethodDefinitionHandle methodHandle in definition.GetMethods())
        {
            MethodDefinition method = _metadata.GetMethodDefinition(methodHandle);
            if (!string.Equals(_metadata.GetString(method.Name), methodName, StringComparison.Ordinal))
                continue;
            // Param 表里有返回值那一行（SequenceNumber = 0），真正的形参只数序号非 0 的。
            if (CountParameters(method) != parameterCount)
                continue;
            if (method.Attributes.HasFlag(MethodAttributes.Virtual)
                && !method.Attributes.HasFlag(MethodAttributes.NewSlot))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>形参个数：Param 表里 SequenceNumber = 0 的那行是返回值，不计。</summary>
    private int CountParameters(MethodDefinition method)
    {
        int count = 0;
        foreach (ParameterHandle handle in method.GetParameters())
        {
            if (_metadata.GetParameter(handle).SequenceNumber != 0)
                count++;
        }
        return count;
    }

    /// <summary>根播种与指纹名单里的成员是否存在（字段或属性都算）。</summary>
    public bool HasFieldOrProperty(string fullName, string memberName)
    {
        if (!_types.TryGetValue(fullName, out TypeDefinitionHandle handle))
            return false;
        TypeDefinition definition = _metadata.GetTypeDefinition(handle);
        foreach (FieldDefinitionHandle fieldHandle in definition.GetFields())
        {
            FieldDefinition field = _metadata.GetFieldDefinition(fieldHandle);
            if (string.Equals(_metadata.GetString(field.Name), memberName, StringComparison.Ordinal))
                return true;
        }
        foreach (PropertyDefinitionHandle propertyHandle in definition.GetProperties())
        {
            PropertyDefinition property = _metadata.GetPropertyDefinition(propertyHandle);
            if (string.Equals(_metadata.GetString(property.Name), memberName, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>按 AfpReflection.RequireConst 的判据：静态字面量 int 常量。</summary>
    public int? ConstValue(string fullName, string fieldName)
    {
        if (!_types.TryGetValue(fullName, out TypeDefinitionHandle handle))
            return null;
        TypeDefinition definition = _metadata.GetTypeDefinition(handle);
        foreach (FieldDefinitionHandle fieldHandle in definition.GetFields())
        {
            FieldDefinition field = _metadata.GetFieldDefinition(fieldHandle);
            if (!string.Equals(_metadata.GetString(field.Name), fieldName, StringComparison.Ordinal))
                continue;
            if (!field.Attributes.HasFlag(FieldAttributes.Static)
                || !field.Attributes.HasFlag(FieldAttributes.Literal))
            {
                return null;
            }
            ConstantHandle constantHandle = field.GetDefaultValue();
            if (constantHandle.IsNil)
                return null;
            Constant constant = _metadata.GetConstant(constantHandle);
            BlobReader value = _metadata.GetBlobReader(constant.Value);
            return constant.TypeCode switch
            {
                ConstantTypeCode.SByte => value.ReadSByte(),
                ConstantTypeCode.Byte => value.ReadByte(),
                ConstantTypeCode.Int16 => value.ReadInt16(),
                ConstantTypeCode.UInt16 => value.ReadUInt16(),
                ConstantTypeCode.Int32 => value.ReadInt32(),
                ConstantTypeCode.UInt32 => (int)value.ReadUInt32(),
                ConstantTypeCode.Int64 => (int)value.ReadInt64(),
                _ => null,
            };
        }
        return null;
    }
}
