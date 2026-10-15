# Changelog
All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](http://keepachangelog.com/en/1.0.0/)
and this project adheres to [Semantic Versioning](http://semver.org/spec/v2.0.0.html).

## 0.1.1
- Renaming several members referenced from one graph (a Base/Derived hierarchy, "rename overloads", a property with its backing field) no longer corrupts the asset: each graph gets one combined edit
- Type and namespace renames touch only type names in graphs: node names, dialogue text, variable names and assembly names (an asmdef named like the namespace) stay as they are
- Members of a generic base class (e.g. `Singleton<T>`) with parameters of type `T` are resolved: Code Vision, Find Usages and Rename work, no false "overload no longer exists" warning
- The unresolved-reference warning is reported once on a partial class, not on every part
- Graph changes can be excluded in the Rename preview
- YAML: correct line folding of quoted scalars and all double-quoted escapes; FullSerializer escapes `\a` and `\0` are accepted

## 0.1.0
- Background index of C# member references (`_baseInfo`) in NodeCanvas graph assets and in graphs bound to GraphOwner components in prefabs and scenes, including references inside Missing* wrappers
- Code Vision "NodeCanvas: N usages" above methods, properties and fields (overloads counted separately), with a popup that navigates to the asset and, with a connected Unity editor, opens it in Unity
- Find Usages: graph references in a separate "NodeCanvas graphs" group
- Rename of methods, properties, fields, types and namespaces updates the graphs byte-for-byte as Unity writes them, in the same undoable refactoring
- Members used only by graphs are no longer reported as unused
- Warning when a graph references a member that no longer exists or whose overload changed
- Warning on a type whose old full name is still used by graphs (after Move to namespace / Move type to outer scope), with a quick-fix that updates the graphs
- Settings page (Languages & Frameworks › NodeCanvas Usages): enable indexing, include bound graphs
