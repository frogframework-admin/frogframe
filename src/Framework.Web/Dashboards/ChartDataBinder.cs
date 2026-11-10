using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;

namespace Framework.Web.Dashboards;

/// <summary>
/// Converts QueryResultSet rows into a typed dynamic list suitable for BlazorBlueprint / ECharts data extraction.
/// Types are generated once per column signature and cached in memory.
/// </summary>
public static class ChartDataBinder
{
    private static readonly ModuleBuilder ModuleBuilder;
    private static readonly ConcurrentDictionary<string, Type> TypeCache = new();

    static ChartDataBinder()
    {
        var asmName = new AssemblyName("Framework.Web.DynamicChartTypes");
        var asmBuilder = AssemblyBuilder.DefineDynamicAssembly(asmName, AssemblyBuilderAccess.Run);
        ModuleBuilder = asmBuilder.DefineDynamicModule("ChartTypesModule");
    }

    public static System.Collections.IList BindRows(IReadOnlyList<string> columnNames, IReadOnlyList<object?[]> rows)
    {
        var sanitizedCols = columnNames.Select((c, i) => SanitizeIdentifier(c, i)).ToList();
        var cacheKey = string.Join("|", sanitizedCols);

        var rowType = TypeCache.GetOrAdd(cacheKey, _ =>
        {
            var typeName = "DynamicChartRow_" + Guid.NewGuid().ToString("N");
            var typeBuilder = ModuleBuilder.DefineType(typeName, TypeAttributes.Public | TypeAttributes.Class);

            foreach (var col in sanitizedCols)
            {
                var field = typeBuilder.DefineField("_" + col, typeof(object), FieldAttributes.Private);
                var prop = typeBuilder.DefineProperty(col, PropertyAttributes.HasDefault, typeof(object), null);

                var getter = typeBuilder.DefineMethod("get_" + col,
                    MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
                    typeof(object), Type.EmptyTypes);
                var getIL = getter.GetILGenerator();
                getIL.Emit(OpCodes.Ldarg_0);
                getIL.Emit(OpCodes.Ldfld, field);
                getIL.Emit(OpCodes.Ret);
                prop.SetGetMethod(getter);

                var setter = typeBuilder.DefineMethod("set_" + col,
                    MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
                    null, new[] { typeof(object) });
                var setIL = setter.GetILGenerator();
                setIL.Emit(OpCodes.Ldarg_0);
                setIL.Emit(OpCodes.Ldarg_1);
                setIL.Emit(OpCodes.Stfld, field);
                setIL.Emit(OpCodes.Ret);
                prop.SetSetMethod(setter);
            }

            return typeBuilder.CreateType();
        });

        var props = sanitizedCols.Select(c => rowType.GetProperty(c)!).ToList();
        var listType = typeof(List<>).MakeGenericType(rowType);
        var list = (System.Collections.IList)Activator.CreateInstance(listType)!;

        foreach (var r in rows)
        {
            var instance = Activator.CreateInstance(rowType)!;
            for (var i = 0; i < columnNames.Count && i < r.Length; i++)
            {
                var val = r[i];
                // normalize numeric values to double for ECharts calculations
                if (val is decimal d) val = (double)d;
                else if (val is long l) val = (double)l;
                else if (val is int intVal) val = (double)intVal;
                props[i].SetValue(instance, val);
            }
            list.Add(instance);
        }

        return list;
    }

    private static string SanitizeIdentifier(string name, int index)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Col" + index;
        var chars = name.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
        var clean = new string(chars);
        if (!char.IsLetter(clean[0]) && clean[0] != '_') clean = "_" + clean;
        return clean;
    }
}

