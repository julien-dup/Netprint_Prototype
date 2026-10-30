using System.Reflection;
using NetArchTest.Rules;
using NetPrint.Modules.Ventes;
using NetPrint.Noyau;
using NetPrint.Socle.Session;

namespace NetPrint.Architecture.Tests;

// Règles du monolithe modulaire (cadrage, « Structure de la solution ») :
//   1. Dépendances dans un seul sens : Module → Noyau → Socle.
//   2. Deux modules ne se référencent jamais ; ils passent par les Contrats de l'autre.
//   3. Les écrans n'accèdent jamais à la base : ils passent par les services du module.
public class ReglesArchitecture
{
    private static readonly Assembly Socle = typeof(SessionNetPrint).Assembly;
    private static readonly Assembly Noyau = typeof(NoyauModule).Assembly;

    // À compléter à chaque nouveau module
    private static readonly Assembly[] Modules =
    [
        typeof(VentesModule).Assembly,
    ];

    public static TheoryData<string> NomsModules()
    {
        var donnees = new TheoryData<string>();
        foreach (var m in Modules) donnees.Add(m.GetName().Name!);
        return donnees;
    }

    private static Assembly Module(string nom) => Modules.Single(m => m.GetName().Name == nom);

    [Fact]
    public void Le_socle_ne_depend_ni_du_noyau_ni_des_modules()
    {
        var r = Types.InAssembly(Socle).ShouldNot().HaveDependencyOnAny("NetPrint.Noyau", "NetPrint.Modules").GetResult();
        Assert.True(r.IsSuccessful, Detail(r));
    }

    [Fact]
    public void Le_noyau_ne_depend_d_aucun_module()
    {
        var r = Types.InAssembly(Noyau).ShouldNot().HaveDependencyOn("NetPrint.Modules").GetResult();
        Assert.True(r.IsSuccessful, Detail(r));
    }

    [Theory]
    [MemberData(nameof(NomsModules))]
    public void Un_module_ne_reference_que_les_contrats_des_autres_modules(string nomModule)
    {
        var interdits = Module(nomModule).GetReferencedAssemblies()
            .Select(a => a.Name ?? "")
            .Where(n => n.StartsWith("NetPrint.Modules.")
                        && !n.EndsWith(".Contrats")
                        && n != nomModule)
            .ToList();
        Assert.True(interdits.Count == 0, $"{nomModule} référence directement : {string.Join(", ", interdits)}");
    }

    [Theory]
    [MemberData(nameof(NomsModules))]
    public void Les_ecrans_n_accedent_jamais_a_la_base(string nomModule)
    {
        var r = Types.InAssembly(Module(nomModule))
            .That().ResideInNamespace($"{nomModule}.Ecrans")
            .ShouldNot().HaveDependencyOnAny("System.Data", "Npgsql", "NetPrint.Socle.Donnees", $"{nomModule}.Donnees")
            .GetResult();
        Assert.True(r.IsSuccessful, Detail(r));
    }

    private static string Detail(TestResult r) =>
        r.IsSuccessful ? "" : "Types en infraction : " + string.Join(", ", r.FailingTypeNames ?? []);
}
