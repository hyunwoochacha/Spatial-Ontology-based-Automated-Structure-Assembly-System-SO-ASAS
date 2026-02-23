# SO-ASAS: Spatial Ontology-based Automated Structure Assembly System

A spatial ontology-based automated assembly system for BIM library components. This system defines spatial relationships among bridge structural elements using OWL/RDF ontology and derives assembly sequences through SPARQL queries.

## Overview

SO-ASAS formalizes the spatial relationships of bridge structural components (piers, abutments, slabs, protective walls) as an OWL ontology in Turtle (.ttl) format. By querying the ontology with SPARQL, the system automatically determines the assembly order and spatial positioning of each component within a BIM environment.

### System Architecture

```
┌──────────────────────────────────────────────────────┐
│                                                      │
│  ┌──────────────┐   ┌─────────────┐   ┌───────────┐ │
│  │  BIM Library │◄──│  Assembly   │◄──│  SPARQL   │ │
│  │  Components  │   │  Engine     │   │  Processor│ │
│  └──────────────┘   └─────────────┘   └─────┬─────┘ │
│                                              │       │
│                                       ┌──────┴─────┐ │
│                                       │  OWL/RDF   │ │
│                                       │  Spatial   │ │
│                                       │  Ontology  │ │
│                                       └────────────┘ │
└──────────────────────────────────────────────────────┘
```

## Ontology Design

### Class Hierarchy

```turtle
ex:WBS_Component
 └── ex:Bridge
      ├── ex:SuperStructure
      │    ├── ex:Slab
      │    └── ex:Protectivewall
      └── ex:SubStructure
           ├── ex:Abutment
           │    ├── ex:AbutmentFooting
           │    ├── ex:AbutmentFoundation
           │    ├── ex:AbutmentWall
           │    ├── ex:AbutmentWingWall
           │    └── ex:AbutmentCap
           └── ex:Pier
                ├── ex:PierFooting
                ├── ex:PierFoundation
                ├── ex:PierColumn
                └── ex:PierCoping
```

### Spatial Relationship Properties

| Property | Description | Example |
|----------|-------------|---------|
| `ex:isAttachedTo` | Structural connection between vertically stacked components | PierCoping → PierColumn |
| `ex:isAdjacentOf` | Lateral adjacency relationship | WingWall → AbutmentWall |
| `ex:isPuttingOn` | Component placed on top of another | ProtectiveWall → Slab |
| `ex:isPartOf` | Part-whole composition | All components → Bridge |
| `ex:isBearingWith` | Load-bearing support relationship | PierCoping → Slab |

### Data Properties

| Property | Type | Description |
|----------|------|-------------|
| `ex:name` | xsd:string | Component identifier |
| `ex:material` | xsd:string | Material type |
| `ex:staLocation` | xsd:string | Station location |

## SPARQL Query Examples

### Substructure Assembly Query

Extracts component names by traversing `isAttachedTo` relationships from top to bottom:

```sparql
PREFIX ex: <http://example.org/>

SELECT ?copingName ?columnName ?foundationName ?footingName WHERE {
    ex:A1_PierCoping_Instance ex:isAttachedTo ex:A1_PierColumn_Instance .
    ex:A1_PierColumn_Instance ex:isAttachedTo ex:A1_PierFoundation_Instance .
    ex:A1_PierFoundation_Instance ex:isAttachedTo ex:A1_PierFooting_Instance .
    ex:A1_PierCoping_Instance ex:name ?copingName .
    ex:A1_PierColumn_Instance ex:name ?columnName .
    ex:A1_PierFoundation_Instance ex:name ?foundationName .
    ex:A1_PierFooting_Instance ex:name ?footingName .
}
```

### Superstructure Assembly Query

```sparql
PREFIX ex: <http://example.org/>

SELECT ?slabName ?protectivewallLeftName ?protectivewallRightName WHERE {
    ex:Slab_Instance ex:name ?slabName .
    ex:Protectivewall_Left_Instance ex:name ?protectivewallLeftName .
    ex:Protectivewall_Right_Instance ex:name ?protectivewallRightName .
}
```

## Raw Data

The ontology source file for verification is available in the [`ontology/`](ontology/) folder:

| File | Description |
|------|-------------|
| [`WBS_Bridge.ttl`](ontology/WBS_Bridge.ttl) | Unified bridge ontology (Piers A1–A5, Abutments A1–A2, Superstructure) |

## Requirements

- Autodesk Revit 2022+
- .NET Framework 4.8
- [dotNetRDF](https://www.dotnetrdf.org/) (VDS.RDF)

## License

MIT License
