using System.Reflection;
using TruckLib.ScsMap;

// Prints the public surface of the map-data types as they exist in the installed host assemblies.
// The question it exists to answer: "can a plugin get real geometry out of this class?" - decided by
// reading the binary that is actually loaded, not a source snapshot and not a guess at member names.

Console.OutputEncoding = System.Text.Encoding.UTF8;

Assembly scs = typeof(Map).Assembly;
Assembly models = typeof(TruckLib.Models.Ppd.PrefabDescriptor).Assembly;

Console.WriteLine($"# {scs.GetName().Name}  location={scs.Location}");
Console.WriteLine($"# {models.GetName().Name}  location={models.Location}");
Console.WriteLine();

Console.WriteLine("## ScsMap item classes (public instance members)");
foreach (Type type in scs.GetTypes().Where(t => t.IsPublic && typeof(IMapObject).IsAssignableFrom(t))
                            .OrderBy(t => t.Name, StringComparer.Ordinal))
{
    Console.WriteLine($"{TypeName(type)} : {TypeName(type.BaseType)}");
    foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                                         .OrderBy(p => p.Name, StringComparer.Ordinal))
    {
        Console.WriteLine($"    {TypeName(property.PropertyType)} {property.Name}");
    }

    foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                                    .OrderBy(f => f.Name, StringComparer.Ordinal))
    {
        Console.WriteLine($"    (field) {TypeName(field.FieldType)} {field.Name}");
    }
}

Console.WriteLine();
Console.WriteLine("## Model / PPD types (what a prefab or model description can give us)");
foreach (Type type in models.GetTypes().Where(t => t.IsPublic && !t.IsEnum).OrderBy(t => t.FullName, StringComparer.Ordinal))
{
    Console.WriteLine($"{TypeName(type)} : {TypeName(type.BaseType)}");
    foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                                         .OrderBy(p => p.Name, StringComparer.Ordinal))
    {
        Console.WriteLine($"    {TypeName(property.PropertyType)} {property.Name}");
    }
}

static string TypeName(Type? type)
{
    if (type == null)
        return "-";

    string name = type.IsGenericType
        ? type.Name.Substring(0, type.Name.IndexOf('`')) + "<" + string.Join(",", type.GetGenericArguments().Select(TypeName)) + ">"
        : type.Name;

    return type.IsNested ? TypeName(type.DeclaringType) + "+" + name : name;
}
