using NetPrint.Socle.Documents;

namespace NetPrint.Modules.Logistique.Contrats;

// =====================================================================
// Éditions du pré-BL (WinDev PreBL : « Imprimer » → EXP530NAV → EXP535P2) en PDF natif, sans rien écrire.
// =====================================================================
public interface IEditionPreBl
{
    // Le pré-BL, présenté comme le BL qu'il deviendra (même mise en page que le BL)
    Task<DocumentGenere> EditerAsync(int numero, bool apercu, CancellationToken ct = default);
}
