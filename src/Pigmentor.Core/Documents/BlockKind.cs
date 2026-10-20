namespace Pigmentor.Core.Documents;

/// <summary>
/// Specifies the structural kind of an extracted document block.
/// </summary>
public enum BlockKind
{
    /// <summary>Prose paragraph or body text.</summary>
    Text,

    /// <summary>Section or document heading.</summary>
    Heading,

    /// <summary>Structured tabular data.</summary>
    Table,

    /// <summary>Graphic, diagram, schematic, or photo.</summary>
    Figure,

    /// <summary>Preformatted code, configuration, or raw telemetry/register dump.</summary>
    Code,

    /// <summary>Bullet or numbered list item.</summary>
    List
}

