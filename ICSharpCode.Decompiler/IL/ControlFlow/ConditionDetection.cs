﻿// Copyright (c) 2014 Daniel Grunwald
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
using System.Collections;
using System.Diagnostics;
using System.Linq;
using ICSharpCode.Decompiler.FlowAnalysis;

namespace ICSharpCode.Decompiler.IL.ControlFlow
{
	/// <summary>
	/// Detects 'if' structure and other non-loop aspects of control flow.
	/// </summary>
	/// <remarks>
	/// Order dependency: should run after loop detection.
	/// Blocks should be basic blocks prior to this transform.
	/// After this transform, they will be extended basic blocks.
	/// </remarks>
	public class ConditionDetection : IILTransform
	{
		public void Run(ILFunction function, ILTransformContext context)
		{
			foreach (var container in function.Descendants.OfType<BlockContainer>()) {
				Run(container, context);
			}
		}
		
		BlockContainer currentContainer;
		ControlFlowNode[] controlFlowGraph;
		
		void Run(BlockContainer container, ILTransformContext context)
		{
			currentContainer = container;
			controlFlowGraph = LoopDetection.BuildCFG(container);
			Dominance.ComputeDominance(controlFlowGraph[0], context.CancellationToken);
			BuildConditionStructure(controlFlowGraph[0]);
			controlFlowGraph = null;
			currentContainer = null;
			container.Blocks.RemoveAll(b => b.Parent != container || b.Instructions.Count == 0);
		}

		/// <summary>
		/// Builds structured control flow for the block associated with the control flow node.
		/// </summary>
		/// <remarks>
		/// After a block was processed, it should use structured control flow
		/// and have just a single 'regular' exit point (last branch instruction in the block)
		/// </remarks>
		void BuildConditionStructure(ControlFlowNode cfgNode)
		{
			Block block = (Block)cfgNode.UserData;
			// First, process the children in the dominator tree.
			// This ensures that blocks being embedded into this block are already fully processed.
			foreach (var child in cfgNode.DominatorTreeChildren)
				BuildConditionStructure(child);
			// Last instruction is one with unreachable endpoint
			// (guaranteed by combination of BlockContainer and Block invariants)
			Debug.Assert(block.Instructions.Last().HasFlag(InstructionFlags.EndPointUnreachable));
			ILInstruction exitInst = block.Instructions.Last();
			
			// Previous-to-last instruction might have conditional control flow,
			// usually an IfInstruction with a branch:
			IfInstruction ifInst = block.Instructions.ElementAtOrDefault(block.Instructions.Count - 2) as IfInstruction;
			if (ifInst != null && ifInst.FalseInst.OpCode == OpCode.Nop) {
				if (IsBranchToLaterTarget(ifInst.TrueInst, exitInst)) {
					// "if (c) goto lateBlock; goto earlierBlock;"
					// -> "if (!c)" goto earlierBlock; goto lateBlock;
					// This reordering should make the if structure correspond more closely to the original C# source code
					block.Instructions[block.Instructions.Count - 1] = ifInst.TrueInst;
					ifInst.TrueInst = exitInst;
					exitInst = block.Instructions.Last();
					ifInst.Condition = new LogicNot(ifInst.Condition);
				}
				
				ILInstruction trueExitInst;
				if (IsUsableBranchToChild(cfgNode, ifInst.TrueInst)) {
					// "if (...) goto targetBlock; exitInst;"
					// -> "if (...) { targetBlock } exitInst;"
					var targetBlock = ((Branch)ifInst.TrueInst).TargetBlock;
					// The targetBlock was already processed, we can embed it into the if statement:
					ifInst.TrueInst = targetBlock;
					trueExitInst = targetBlock.Instructions.LastOrDefault();
					if (CompatibleExitInstruction(exitInst, trueExitInst)) {
						// "if (...) { ...; goto exitPoint } goto exitPoint;"
						// -> "if (...) { ... } goto exitPoint;"
						targetBlock.Instructions.RemoveAt(targetBlock.Instructions.Count - 1);
						trueExitInst = null;
					}
				} else {
					trueExitInst = ifInst.TrueInst;
				}
				if (IsUsableBranchToChild(cfgNode, exitInst)) {
					var targetBlock = ((Branch)exitInst).TargetBlock;
					var falseExitInst = targetBlock.Instructions.LastOrDefault();
					if (CompatibleExitInstruction(trueExitInst, falseExitInst)) {
						// if (...) { ...; goto exitPoint; } goto nextBlock; nextBlock: ...; goto exitPoint;
						// -> if (...) { ... } else { ... } goto exitPoint;
						targetBlock.Instructions.RemoveAt(targetBlock.Instructions.Count - 1);
						ifInst.FalseInst = targetBlock;
						exitInst = block.Instructions[block.Instructions.Count - 1] = falseExitInst;
						Block trueBlock = ifInst.TrueInst as Block;
						if (trueBlock != null) {
							Debug.Assert(trueExitInst == trueBlock.Instructions.Last());
							trueBlock.Instructions.RemoveAt(trueBlock.Instructions.Count - 1);
						} else {
							Debug.Assert(trueExitInst == ifInst.TrueInst);
							ifInst.TrueInst = new Nop { ILRange = ifInst.TrueInst.ILRange };
						}
					}
				}
				if (ifInst.FalseInst.OpCode != OpCode.Nop && ifInst.FalseInst.ILRange.Start < ifInst.TrueInst.ILRange.Start
				    || ifInst.TrueInst.OpCode == OpCode.Nop)
				{
					// swap true and false branches of if, to bring them in the same order as the IL code
					var oldTrue = ifInst.TrueInst;
					ifInst.TrueInst = ifInst.FalseInst;
					ifInst.FalseInst = oldTrue;
					if (ifInst.Condition.OpCode == OpCode.LogicNot)
						ifInst.Condition = ifInst.Condition.Children.Single();
					else
						ifInst.Condition = new LogicNot(ifInst.Condition);
				}
			}
			if (IsUsableBranchToChild(cfgNode, exitInst)) {
				// "...; goto usableblock;"
				// -> embed target block in this block
				var targetBlock = ((Branch)exitInst).TargetBlock;
				Debug.Assert(exitInst == block.Instructions.Last());
				block.Instructions.RemoveAt(block.Instructions.Count - 1);
				block.Instructions.AddRange(targetBlock.Instructions);
				targetBlock.Instructions.Clear();
			}
		}

		bool IsBranchToLaterTarget(ILInstruction inst1, ILInstruction inst2)
		{
			Block block1, block2;
			if (inst1.MatchBranch(out block1) && inst2.MatchBranch(out block2)) {
				return block1.ILRange.Start > block2.ILRange.Start;
			}
			return false;
		}
		
		bool IsUsableBranchToChild(ControlFlowNode cfgNode, ILInstruction potentialBranchInstruction)
		{
			Branch br = potentialBranchInstruction as Branch;
			if (br == null)
				return false;
			var targetBlock = br.TargetBlock;
			return targetBlock.Parent == currentContainer && cfgNode.Dominates(controlFlowGraph[targetBlock.ChildIndex])
				&& targetBlock.IncomingEdgeCount == 1 && targetBlock.FinalInstruction.OpCode == OpCode.Nop;
		}
		
		internal static bool CompatibleExitInstruction(ILInstruction exit1, ILInstruction exit2)
		{
			if (exit1 == null || exit2 == null || exit1.OpCode != exit2.OpCode)
				return false;
			switch (exit1.OpCode) {
				case OpCode.Branch:
					Branch br1 = (Branch)exit1;
					Branch br2 = (Branch)exit2;
					return br1.TargetBlock == br2.TargetBlock;
				case OpCode.Leave:
					Leave leave1 = (Leave)exit1;
					Leave leave2 = (Leave)exit2;
					return leave1.TargetContainer == leave2.TargetContainer;
				case OpCode.Return:
					Return ret1 = (Return)exit1;
					Return ret2 = (Return)exit2;
					return ret1.ReturnValue == null && ret2.ReturnValue == null;
				default:
					return false;
			}
		}
	}
}