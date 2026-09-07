using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SmartX.Models;

namespace SmartX.Services;

/// <summary>
/// Service that uses RECURSION to validate nested device deployment trees
/// and multi-tier configuration profiles.
/// e.g. validating that a node is safely configured within Sub-Zone B -> Zone 1 -> Facility A
/// </summary>
public class DeploymentValidationService
{
    private DeploymentNode _root;

    public DeploymentValidationService()
    {
        _root = BuildDefaultTree();
    }

    public DeploymentNode GetRoot() => _root;

    /// <summary>
    /// RECURSIVE validation algorithm that traverses the entire deployment tree.
    /// Checks: IsConfigured flag, capacity constraints, and validates all children.
    /// </summary>
    public ValidationResult ValidateTree(DeploymentNode? node = null, string path = "", int depth = 0)
    {
        node ??= _root;
        var result = new ValidationResult
        {
            ValidationPath = path,
            Depth = depth
        };

        // Base case: null node is valid (leaf with no children)
        if (node == null)
            return result;

        string currentPath = string.IsNullOrEmpty(path) ? node.Name : $"{path} -> {node.Name}";
        result.ValidationPath = currentPath;
        result.NodesValidated = 1;
        result.Depth = depth;

        // ─── Validate this node ───
        if (!node.IsConfigured)
        {
            result.Errors.Add($"[DEPTH {depth}] {currentPath}: Node '{node.Name}' is not configured.");
            result.IsValid = false;
        }

        if (node.CurrentLoad > node.MaxCapacity)
        {
            result.Errors.Add($"[DEPTH {depth}] {currentPath}: Node '{node.Name}' exceeds capacity ({node.CurrentLoad}/{node.MaxCapacity}).");
            result.IsValid = false;
        }
        else if (node.CurrentLoad > node.MaxCapacity * 0.85)
        {
            result.Warnings.Add($"[DEPTH {depth}] {currentPath}: Node '{node.Name}' near capacity ({node.CurrentLoad}/{node.MaxCapacity}).");
        }

        // ─── RECURSIVE CASE: validate all children ───
        foreach (var child in node.Children)
        {
            var childResult = ValidateTree(child, currentPath, depth + 1);

            // Merge child results into parent result
            result.IsValid = result.IsValid && childResult.IsValid;
            result.Errors.AddRange(childResult.Errors);
            result.Warnings.AddRange(childResult.Warnings);
            result.NodesValidated += childResult.NodesValidated;
            result.Depth = Math.Max(result.Depth, childResult.Depth);
        }

        return result;
    }

    /// <summary>
    /// RECURSIVE search: find a node by name/id in the deployment tree.
    /// </summary>
    public DeploymentNode? FindNode(string name, DeploymentNode? node = null)
    {
        node ??= _root;

        if (node == null) return null;

        // Base case: found the node
        if (node.Name == name || node.NodeId == name)
            return node;

        // Recursive case: search children
        foreach (var child in node.Children)
        {
            var found = FindNode(name, child);
            if (found != null)
                return found;
        }

        return null; // Not found in this subtree
    }

    /// <summary>
    /// RECURSIVE node count.
    /// </summary>
    public int CountNodes(DeploymentNode? node = null)
    {
        node ??= _root;
        if (node == null) return 0;

        // Base: 1 for this node + recursive count of children
        int count = 1;
        foreach (var child in node.Children)
        {
            count += CountNodes(child);
        }
        return count;
    }

    /// <summary>
    /// RECURSIVE tree rendering for display.
    /// </summary>
    public string RenderTree(DeploymentNode? node = null, int indent = 0)
    {
        node ??= _root;
        if (node == null) return "";

        var sb = new StringBuilder();
        string prefix = new string(' ', indent * 3) + (indent > 0 ? "└─ " : "");
        string status = node.IsValid ? "✅" : "❌";
        sb.AppendLine($"{prefix}{status} {node.NodeType}: {node.Name} (Load: {node.CurrentLoad}/{node.MaxCapacity})");

        foreach (var child in node.Children)
        {
            sb.Append(RenderTree(child, indent + 1));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Build the default deployment tree for the Smart-X system.
    /// Facility A -> Zone 1 / Zone 2 -> Sub-Zones -> Rooms -> Nodes
    /// </summary>
    private DeploymentNode BuildDefaultTree()
    {
        return new DeploymentNode
        {
            NodeId = "FAC-A",
            Name = "Facility A",
            NodeType = "Facility",
            IsConfigured = true,
            MaxCapacity = 500,
            CurrentLoad = 120,
            Children = new List<DeploymentNode>
            {
                new DeploymentNode
                {
                    NodeId = "Z1",
                    Name = "Zone 1",
                    NodeType = "Zone",
                    IsConfigured = true,
                    MaxCapacity = 200,
                    CurrentLoad = 80,
                    Children = new List<DeploymentNode>
                    {
                        new DeploymentNode
                        {
                            NodeId = "SZ-A1",
                            Name = "Sub-Zone A",
                            NodeType = "SubZone",
                            IsConfigured = true,
                            MaxCapacity = 50,
                            CurrentLoad = 20,
                            Children = new List<DeploymentNode>
                            {
                                new DeploymentNode { NodeId = "R101", Name = "Room 101", NodeType = "Room", IsConfigured = true, MaxCapacity = 15, CurrentLoad = 5 },
                                new DeploymentNode { NodeId = "R102", Name = "Room 102", NodeType = "Room", IsConfigured = true, MaxCapacity = 15, CurrentLoad = 8 },
                                new DeploymentNode { NodeId = "R103", Name = "Room 103", NodeType = "Room", IsConfigured = false, MaxCapacity = 20, CurrentLoad = 7 }
                            }
                        },
                        new DeploymentNode
                        {
                            NodeId = "SZ-B1",
                            Name = "Sub-Zone B",
                            NodeType = "SubZone",
                            IsConfigured = true,
                            MaxCapacity = 80,
                            CurrentLoad = 60,
                            Children = new List<DeploymentNode>
                            {
                                new DeploymentNode { NodeId = "R201", Name = "Room 201", NodeType = "Room", IsConfigured = true, MaxCapacity = 25, CurrentLoad = 22 },
                                new DeploymentNode { NodeId = "R202", Name = "Room 202", NodeType = "Room", IsConfigured = true, MaxCapacity = 25, CurrentLoad = 18 },
                                new DeploymentNode { NodeId = "N-A1", Name = "Node A1", NodeType = "Node", IsConfigured = true, MaxCapacity = 10, CurrentLoad = 10 },
                                new DeploymentNode { NodeId = "N-A2", Name = "Node A2", NodeType = "Node", IsConfigured = true, MaxCapacity = 10, CurrentLoad = 12 }  // Over capacity!
                            }
                        }
                    }
                },
                new DeploymentNode
                {
                    NodeId = "Z2",
                    Name = "Zone 2",
                    NodeType = "Zone",
                    IsConfigured = true,
                    MaxCapacity = 200,
                    CurrentLoad = 40,
                    Children = new List<DeploymentNode>
                    {
                        new DeploymentNode
                        {
                            NodeId = "SZ-C2",
                            Name = "Sub-Zone C",
                            NodeType = "SubZone",
                            IsConfigured = true,
                            MaxCapacity = 100,
                            CurrentLoad = 40,
                            Children = new List<DeploymentNode>
                            {
                                new DeploymentNode { NodeId = "R301", Name = "Room 301", NodeType = "Room", IsConfigured = true, MaxCapacity = 30, CurrentLoad = 10 },
                                new DeploymentNode { NodeId = "R302", Name = "Room 302", NodeType = "Room", IsConfigured = false, MaxCapacity = 30, CurrentLoad = 15 },
                                new DeploymentNode { NodeId = "N-B1", Name = "Node B1", NodeType = "Node", IsConfigured = true, MaxCapacity = 20, CurrentLoad = 15 }
                            }
                        }
                    }
                }
            }
        };
    }
}