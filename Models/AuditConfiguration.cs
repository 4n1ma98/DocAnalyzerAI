namespace DocAnalyzerAI.Models;

public enum AuditPerspective
{
    Worker,  // Trabajador / Contratista (defensa de la parte que presta el servicio)
    Employer // Empresa / Contratante (blindaje legal, propiedad intelectual y mitigación de demandas)
}

public class CountryOption
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string LegalFramework { get; set; } = string.Empty;
}

public class ContractAnnexItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string FileName { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public long Size { get; set; }
}

public class AuditOptions
{
    public string CountryCode { get; set; } = "CO";
    public AuditPerspective Perspective { get; set; } = AuditPerspective.Worker;
    public List<ContractAnnexItem> Annexes { get; set; } = [];

    public static readonly List<CountryOption> AvailableCountries =
    [
        new() { Code = "CO", Name = "Colombia", LegalFramework = "Código Sustantivo del Trabajo (CST vigencia 2026, Ley 2101 de 42h)" },
        new() { Code = "MX", Name = "México", LegalFramework = "Ley Federal del Trabajo (LFT vigencia 2026, NOM-037)" },
        new() { Code = "ES", Name = "España", LegalFramework = "Estatuto de los Trabajadores (RDL 2/2015 actualizado a 2026)" },
        new() { Code = "US", Name = "Estados Unidos", LegalFramework = "FLSA, At-Will Employment, FTC Non-Compete Standards (2026)" },
        new() { Code = "CL", Name = "Chile", LegalFramework = "Código del Trabajo de Chile (Ley 21.561 de 40h vigencia 2026)" },
        new() { Code = "AR", Name = "Argentina", LegalFramework = "Ley de Contrato de Trabajo (LCT N° 20.744 actualizada a 2026)" },
        new() { Code = "PE", Name = "Perú", LegalFramework = "TUO D.Leg. 728 y Ley de Productividad y Competitividad Laboral (2026)" },
        new() { Code = "INTL", Name = "Estándar Internacional", LegalFramework = "Convenios OIT y Principios UNIDROIT de Contratos Comerciales" }
    ];

    public CountryOption GetSelectedCountry() =>
        AvailableCountries.FirstOrDefault(c => c.Code.Equals(CountryCode, StringComparison.OrdinalIgnoreCase)) 
        ?? AvailableCountries[0];
}
