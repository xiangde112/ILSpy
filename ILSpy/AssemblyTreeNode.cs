﻿// Copyright (c) 2011 AlphaSierraPapa for the SharpDevelop Team
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ICSharpCode.TreeView;
using Mono.Cecil;

namespace ICSharpCode.ILSpy
{
	sealed class AssemblyTreeNode : ILSpyTreeNode
	{
		readonly AssemblyList assemblyList;
		readonly string fileName;
		string shortName;
		readonly Task<AssemblyDefinition> assemblyTask;
		readonly List<TypeTreeNode> classes = new List<TypeTreeNode>();
		readonly Dictionary<string, NamespaceTreeNode> namespaces = new Dictionary<string, NamespaceTreeNode>();
		readonly SynchronizationContext syncContext;
		
		public AssemblyTreeNode(string fileName, AssemblyList assemblyList)
		{
			if (fileName == null)
				throw new ArgumentNullException("fileName");
			
			this.fileName = fileName;
			this.assemblyList = assemblyList;
			this.assemblyTask = Task.Factory.StartNew<AssemblyDefinition>(LoadAssembly); // requires that this.fileName is set
			this.shortName = Path.GetFileNameWithoutExtension(fileName);
			this.syncContext = SynchronizationContext.Current;
			
			this.LazyLoading = true;
		}
		
		public string FileName {
			get { return fileName; }
		}
		
		public AssemblyDefinition AssemblyDefinition {
			get { return assemblyTask.Result; }
		}
		
		public override object Text {
			get { return shortName; }
		}
		
		public override object Icon {
			get { return Images.Assembly; }
		}
		
		AssemblyDefinition LoadAssembly()
		{
			// runs on background thread
			ReaderParameters p = new ReaderParameters();
			p.AssemblyResolver = new MyAssemblyResolver(this);
			AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(fileName, p);
			foreach (TypeDefinition type in assembly.MainModule.Types.OrderBy(t => t.FullName)) {
				TypeTreeNode node = new TypeTreeNode(type, this);
				classes.Add(node);
				assemblyList.RegisterTypeNode(node);
			}
			syncContext.Post(
				delegate {
					if (shortName != assembly.Name.Name) {
						shortName = assembly.Name.Name;
						RaisePropertyChanged("Text");
					}
				}, null);
			
			return assembly;
		}
		
		sealed class MyAssemblyResolver : IAssemblyResolver
		{
			readonly AssemblyTreeNode parent;
			
			public MyAssemblyResolver(AssemblyTreeNode parent)
			{
				this.parent = parent;
			}
			
			public AssemblyDefinition Resolve(AssemblyNameReference name)
			{
				var node = parent.LookupReferencedAssembly(name.FullName);
				return node != null ? node.AssemblyDefinition : null;
			}
			
			public AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
			{
				var node = parent.LookupReferencedAssembly(name.FullName);
				return node != null ? node.AssemblyDefinition : null;
			}
			
			public AssemblyDefinition Resolve(string fullName)
			{
				var node = parent.LookupReferencedAssembly(fullName);
				return node != null ? node.AssemblyDefinition : null;
			}
			
			public AssemblyDefinition Resolve(string fullName, ReaderParameters parameters)
			{
				var node = parent.LookupReferencedAssembly(fullName);
				return node != null ? node.AssemblyDefinition : null;
			}
		}
		
		protected override void LoadChildren()
		{
			assemblyTask.Wait();
			ModuleDefinition mainModule = assemblyTask.Result.MainModule;
			this.Children.Add(new ReferenceFolderTreeNode(mainModule, this));
			if (mainModule.HasResources)
				this.Children.Add(new ResourceListTreeNode(mainModule));
			foreach (NamespaceTreeNode ns in namespaces.Values) {
				ns.Children.Clear();
			}
			foreach (TypeTreeNode type in classes) {
				NamespaceTreeNode ns;
				if (!namespaces.TryGetValue(type.Namespace, out ns)) {
					ns = new NamespaceTreeNode(type.Namespace);
					namespaces[type.Namespace] = ns;
				}
				ns.Children.Add(type);
			}
			foreach (NamespaceTreeNode ns in namespaces.Values.OrderBy(n => n.Name)) {
				if (ns.Children.Count > 0)
					this.Children.Add(ns);
			}
		}
		
		public override bool CanDrag(SharpTreeNode[] nodes)
		{
			return nodes.All(n => n is AssemblyTreeNode);
		}
		
		public override bool CanDelete(SharpTreeNode[] nodes)
		{
			return Parent != null && Parent.CanDelete(nodes); // handle deletion in the AssemblyListTreeNode
		}
		
		public override void Delete(SharpTreeNode[] nodes)
		{
			Parent.Delete(nodes); // handle deletion in the AssemblyListTreeNode
		}
		
		public override void DeleteCore(SharpTreeNode[] nodes)
		{
			Parent.DeleteCore(nodes); // handle deletion in the AssemblyListTreeNode
		}
		
		internal const string DataFormat = "ILSpyAssemblies";
		
		public override IDataObject Copy(SharpTreeNode[] nodes)
		{
			DataObject dataObject = new DataObject();
			dataObject.SetData(DataFormat, nodes.OfType<AssemblyTreeNode>().Select(n => n.fileName).ToArray());
			return dataObject;
		}
		
		public AssemblyTreeNode LookupReferencedAssembly(string fullName)
		{
			foreach (AssemblyTreeNode node in assemblyList.Assemblies) {
				if (fullName.Equals(node.AssemblyDefinition.FullName, StringComparison.OrdinalIgnoreCase))
					return node;
			}
			
			var name = AssemblyNameReference.Parse(fullName);
			string file = GacInterop.FindAssemblyInNetGac(name);
			if (file == null) {
				string dir = Path.GetDirectoryName(this.fileName);
				if (File.Exists(Path.Combine(dir, name.Name + ".dll")))
					file = Path.Combine(dir, name.Name + ".dll");
				else if (File.Exists(Path.Combine(dir, name.Name + ".exe")))
					file = Path.Combine(dir, name.Name + ".exe");
			}
			if (file != null) {
				return assemblyList.OpenAssembly(file);
			} else {
				return null;
			}
		}
		
		public override FilterResult Filter(FilterSettings settings)
		{
			// avoid accessing this.AssemblyDefinition (waiting for background thread) if settings.SearchTerm == null
			if (settings.SearchTerm == null || settings.SearchTermMatches(this.AssemblyDefinition.Name.Name))
				return FilterResult.Match;
			else
				return FilterResult.Recurse;
		}
	}
}
