using System;
using System.Collections.Generic;

namespace SmartX.Models;

/// <summary>
/// Represents a node in the deployment tree (e.g. Facility -> Zone -> Sub-Zone).
/// Used for recursive validation of device deployment configurations.
/// </summary>
public class DeploymentNode
{
    public string NodeId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string NodeType { get; set; } = string.Empty; // Facility, Zone, SubZone, Room, Node
    public bool IsConfigured { get; set; } = false;
    public int MaxCapacity { get; set; } = 100;
    public int CurrentLoad { get; set; } = 0;

    // Child nodes form a multi-tier tree
    public List<DeploymentNode> Children { get; set; } = new();

    // Validation rule: node must be within capacity and properly configured
    public bool IsValid => IsConfigured && CurrentLoad <= MaxCapacity;

    public override string ToString() => $"{NodeType}: {Name} (Load: {CurrentLoad}/{MaxCapacity})";
}

/// <summary>
/// Result of recursive validation.
/// </summary>
public class ValidationResult
{
    public bool IsValid { get; set; } = true;
    public List<string> Errors { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public int NodesValidated { get; set; }
    public int Depth { get; set; }
    public string ValidationPath { get; set; } = string.Empty;
}