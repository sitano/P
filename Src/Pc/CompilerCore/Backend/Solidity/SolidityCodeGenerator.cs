﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Pc.Backend.ASTExt;
using Microsoft.Pc.TypeChecker;
using Microsoft.Pc.TypeChecker.AST;
using Microsoft.Pc.TypeChecker.AST.Declarations;
using Microsoft.Pc.TypeChecker.AST.Expressions;
using Microsoft.Pc.TypeChecker.AST.Statements;
using Microsoft.Pc.TypeChecker.AST.States;
using Microsoft.Pc.TypeChecker.Types;

namespace Microsoft.Pc.Backend.Solidity
{
    public class SolidityCodeGenerator : ICodeGenerator
    {
        // Name of the contract we are processing
        string ContractName;
        string EventTypeName;

        // Global unique next type identifier
        int TypeId = 0;
        
        // Assign a unique id to each type
        Dictionary<string, int> TypeIdMap = new Dictionary<string, int>();

        // Map each type id to the set of associated variables
        Dictionary<int, Dictionary<string, string>> IdVarsMap = new Dictionary<int, Dictionary<string, string>>();

        HashSet<string> KnownPayloadTypes = new HashSet<string>();
        Dictionary<int, Dictionary<string, string>> NextStateMap = new Dictionary<int, Dictionary<string, string>>();
        Dictionary<int, Dictionary<string, string>> ActionMap = new Dictionary<int, Dictionary<string, string>>();

        public IEnumerable<CompiledFile> GenerateCode(ICompilationJob job, Scope globalScope)
        {
            var context = new CompilationContext(job);
            CompiledFile soliditySource = GenerateSource(context, globalScope);
            return new List<CompiledFile> { soliditySource };
        }
    
        private CompiledFile GenerateSource(CompilationContext context, Scope globalScope)
        {
            var source = new CompiledFile(context.FileName);
        
            WriteSourcePrologue(context, source.Stream);

            foreach (IPDecl decl in globalScope.AllDecls)
            {
                WriteDecl(context, source.Stream, decl);
            }

            // TODO: generate tuple type classes.

            // TODO:
            //WriteSourceEpilogue(context, source.Stream);
            
            return source;
        }

        private void WriteSourcePrologue(CompilationContext context, StringWriter output)
        {
            context.WriteLine(output, "pragma solidity ^0.4.24;");
        }

        
        private void WriteDecl(CompilationContext context, StringWriter output, IPDecl decl)
        {
            string declName = context.Names.GetNameForDecl(decl);
            ContractName = declName;
            EventTypeName = ContractName + "_Event";

            switch (decl)
            {
                case PEvent pEvent when !pEvent.IsBuiltIn:
                    AddEventType(context, pEvent);
                    break;

                case Machine machine:
                    context.WriteLine(output, $"contract {declName}");
                    context.WriteLine(output, "{");
                    WriteMachine(context, output, machine);
                    context.WriteLine(output, "}");
                    break;
                
                default:
                    context.WriteLine(output, $"// TODO: {decl.GetType().Name} {declName}");
                    break;
            }
            
        }

        private void AddEventType(CompilationContext context, PEvent pEvent)
        {
            // Assign a new id to the event type
            int typeId = TypeId++;
            TypeIdMap.Add(pEvent.Name, typeId);

            Dictionary<string, string> varsForId = new Dictionary<string, string>();

            // If there is a new payload type, add it to known payload types
            if (!pEvent.PayloadType.IsSameTypeAs(PrimitiveType.Null))
            {
                string payloadType = GetSolidityType(context, pEvent.PayloadType);

                // TODO: Current only one payload per event seems to be supported
                // create and associate variables with this type
                varsForId.Add(payloadType, pEvent.Name + "_v0");
                
            }
            IdVarsMap.Add(typeId, varsForId);
        }

        private void WriteMachine(CompilationContext context, StringWriter output, Machine machine)
        {
            BuildNextStateMap(context, machine);
            BuildActionMap(context, machine);

            #region variables and data structures
            foreach (Variable field in machine.Fields)
            {
                context.WriteLine(output, $"private {GetSolidityType(context, field.Type)} {context.Names.GetNameForDecl(field)};");
            }

            // Add the queue data structure
            AddInternalDataStructures(context, output, machine);

            #endregion

            #region functions
            
            foreach (Function method in machine.Methods)
            {
                WriteFunction(context, output, method);
            }

            // Add basic fallback function
            AddTransferFunction(context, output);

            // Add helper functions for the queue
            AddInboxEnqDeq(context, output);

            // Add the scheduler
            AddScheduler(context, output, machine);
            #endregion
        }

        private string GetSolidityType(CompilationContext context, PLanguageType returnType)
        {
            switch (returnType.Canonicalize())
            {
                case BoundedType _:
                    return "Machine";
                case EnumType enumType:
                    return context.Names.GetNameForDecl(enumType.EnumDecl);
                case ForeignType _:
                    throw new NotImplementedException();
                case MapType mapType:
                    return $"Dictionary<{GetSolidityType(context, mapType.KeyType)}, {GetSolidityType(context, mapType.ValueType)}>";
                case NamedTupleType _:
                    throw new NotImplementedException();
                case PermissionType _:
                    return "Machine";
                case PrimitiveType primitiveType when primitiveType.IsSameTypeAs(PrimitiveType.Any):
                    return "object";
                case PrimitiveType primitiveType when primitiveType.IsSameTypeAs(PrimitiveType.Bool):
                    return "bool";
                case PrimitiveType primitiveType when primitiveType.IsSameTypeAs(PrimitiveType.Int):
                    return "int";
                case PrimitiveType primitiveType when primitiveType.IsSameTypeAs(PrimitiveType.Float):
                    return "double";
                case PrimitiveType primitiveType when primitiveType.IsSameTypeAs(PrimitiveType.Event):
                    return "struct";
                case PrimitiveType primitiveType when primitiveType.IsSameTypeAs(PrimitiveType.Machine):
                    return "address";
                case PrimitiveType primitiveType when primitiveType.IsSameTypeAs(PrimitiveType.Null):
                    return "void";
                case SequenceType sequenceType:
                    return $"List<{GetSolidityType(context, sequenceType.ElementType)}>";
                case TupleType _:
                    throw new NotImplementedException();
                default:
                    throw new ArgumentOutOfRangeException(nameof(returnType));
            }
        }

        #region internal data structures

        /// <summary>
        /// Adds data structures to encode the P message passing (with run-to-completion) semantics in EVM.
        /// </summary>
        /// <param name="context"></param>
        /// <param name="output"></param>
        /// <param name="machine"></param>
        private void AddInternalDataStructures(CompilationContext context, StringWriter output, Machine machine)
        {
            // Add the event type
            WriteEvent(context, output);

            context.WriteLine(output, $"// Adding inbox for the contract");
            context.WriteLine(output, $"mapping (uint => Event) private inbox;");
            context.WriteLine(output, $"uint private first = 1;");
            context.WriteLine(output, $"uint private last = 0;");
            context.WriteLine(output, $"bool private IsRunning = false;");

            // Add all the states as an enumerated data type
            EnumerateStates(context, output, machine);
        }

        /// <summary>
        /// Add the states as an enumerated data type
        /// </summary>
        /// <param name="context"></param>
        /// <param name="output"></param>
        private void EnumerateStates(CompilationContext context, StringWriter output, Machine machine)
        {
            string startState = "";

            context.WriteLine(output, $"enum State");
            context.WriteLine(output, "{");

            foreach(State state in machine.States)
            {
                if(state.IsStart)
                {
                    startState = GetQualifiedStateName(state);
                }

                context.WriteLine(output, GetQualifiedStateName(state) + ",");
            }

            // Add a system defined error state
            context.WriteLine(output, "Sys_Error_State");
            context.WriteLine(output, "}");

            // Add a variable which tracks the current state of the contract
            context.WriteLine(output, $"State private ContractCurrentState = State." + startState + ";");
        }

        #endregion

        #region queue helper functions
        private void AddInboxEnqDeq(CompilationContext context, StringWriter output)
        {
            // Enqueue to inbox
            context.WriteLine(output, $"// Enqueue in the inbox");
            // TODO: fix the type of the inbox
            context.WriteLine(output, $"function enqueue (" + EventTypeName +" e) private");
            context.WriteLine(output, "{");
            context.WriteLine(output, $"last += 1;");
            context.WriteLine(output, $"inbox[last] = e;");
            context.WriteLine(output, "}");

            // Dequeue from inbox
            context.WriteLine(output, $"// Dequeue from the inbox");
            // TODO: fix the type of the inbox
            context.WriteLine(output, $"function dequeue () private returns (" + EventTypeName + " e)");
            context.WriteLine(output, "{");
            context.WriteLine(output, $"data = inbox[first];");
            context.WriteLine(output, $"delete inbox[first];");
            context.WriteLine(output, $"first += 1;");
            context.WriteLine(output, "}");
        }

        #endregion

        #region scheduler
        private void AddScheduler(CompilationContext context, StringWriter output, Machine machine)
        {
            context.WriteLine(output, $"// Scheduler");
            context.WriteLine(output, $"function scheduler (Event e)  public");
            context.WriteLine(output, "{");
            context.WriteLine(output, $"State memory prevContractState = ContractCurrentState;");
            context.WriteLine(output, $"if(!IsRunning)");
            context.WriteLine(output, "{");
            context.WriteLine(output, $"IsRunning = true;");
            
            for (int i=0; i<TypeId; i++)
            {
                context.WriteLine(output, $"// Perform state change for type with id " + i);

                context.WriteLine(output, $"if(e.typeId == " + i + ")");
                context.WriteLine(output, "{");

                Dictionary<string, string> stateChanges = null;
                Dictionary<string, string> actions = null;

                // Get the set og state changes associated with this event, if any
                if(NextStateMap.ContainsKey(i))
                {
                    stateChanges = NextStateMap[i];
                }
                // Get the action associated with each state, for this event
                if(ActionMap.ContainsKey(i))
                {
                    actions = ActionMap[i];
                }
                
                // Update contract state
                if(stateChanges != null)
                {
                    foreach(string prevState in stateChanges.Keys)
                    {
                        context.WriteLine(output, $"if(prevContractState == State." + prevState + ")");
                        context.WriteLine(output, "{");
                        context.WriteLine(output, $"ContractCurrentState = State." + stateChanges[prevState] + ";");
                        context.WriteLine(output, "}");
                    }
                }

                context.WriteLine(output, $"// Invoke handler for state and type with id " + i);
                // Invoke the handler
                if (actions != null)
                {
                    foreach (string prevState in actions.Keys)
                    {
                        context.WriteLine(output, $"if(prevContractState == State." + prevState + ")");
                        context.WriteLine(output, "{");

                        Dictionary<string, string> varsForId = IdVarsMap[i];

                        if (varsForId.Count == 0)
                        {
                            context.WriteLine(output, $"" + actions[prevState] + "();");
                        }
                        else
                        {
                            string callString = actions[prevState] + "(";
                            foreach(string type in varsForId.Keys)
                            {
                                callString += "e." + varsForId[type] + ",";
                            }
                            callString = callString.Remove(callString.Length - 1);
                            context.WriteLine(output, $"" + callString + ");");
                        }
                        context.WriteLine(output, "}");
                    }
                }
                context.WriteLine(output, "}");
            }
            // enqueue if the contract is busy
            context.WriteLine(output, "}");
            context.WriteLine(output, $"else");
            context.WriteLine(output, "{");
            context.WriteLine(output, $"enqueue(e);");
            context.WriteLine(output, "}");
            context.WriteLine(output, "}");
        }

        #endregion

        #region WriteEvent
        private void WriteEvent(CompilationContext context, StringWriter output)
        {
            context.WriteLine(output, $"// Adding event type");
            context.WriteLine(output, $"struct " + EventTypeName);
            context.WriteLine(output, "{");
            context.WriteLine(output, $"int TypeId;");

            foreach(int typeId in IdVarsMap.Keys)
            {
                Dictionary<string, string> varsForId = IdVarsMap[typeId];

                if(varsForId.Count > 0)
                {
                    foreach(string type in varsForId.Keys)
                    {
                        context.WriteLine(output, $"" + type + " " + varsForId[type] + ";");
                    }
                }
            }
            context.WriteLine(output, "}");
        }
        
        #endregion

        #region WriteFunction

        /// <summary>
        /// Sets up and writes the function signature.
        /// </summary>
        /// <param name="context"></param>
        /// <param name="output"></param>
        /// <param name="function"></param>
        private void WriteFunction(CompilationContext context, StringWriter output, Function function)
        {
            bool isStatic = function.Owner == null;
            FunctionSignature signature = function.Signature;

            string staticKeyword = isStatic ? "static " : "";
            string returnType = GetSolidityType(context, signature.ReturnType);
            string functionName = context.Names.GetNameForDecl(function);
            string functionParameters =
                string.Join(
                    ", ",
                    signature.Parameters.Select(param => $"{GetSolidityType(context, param.Type)} {context.Names.GetNameForDecl(param)}"));

            context.WriteLine(output, $"function {functionName}({functionParameters}) private");
            WriteFunctionBody(context, output, function);
        }

        /// <summary>
        /// Writes the body of a function.
        /// </summary>
        /// <param name="context"></param>
        /// <param name="output"></param>
        /// <param name="function"></param>
        private void WriteFunctionBody(CompilationContext context, StringWriter output, Function function)
        {
            context.WriteLine(output, "{");

            foreach (IPStmt bodyStatement in function.Body.Statements)
            {
                WriteStmt(context, output, bodyStatement);
            }

            context.WriteLine(output, "}");
        }

        #endregion

        #region WriteStmt

        private void WriteStmt(CompilationContext context, StringWriter output, IPStmt stmt)
        {
            switch (stmt)
            {
                case AnnounceStmt announceStmt:
                    break;
                case AssertStmt assertStmt:
                    break;
                case AssignStmt assignStmt:
                    WriteLValue(context, output, assignStmt.Location);
                    context.Write(output, " = ");
                    WriteExpr(context, output, assignStmt.Value);
                    context.WriteLine(output, ";");
                    break;
                case CompoundStmt compoundStmt:
                    context.WriteLine(output, "{");
                    foreach (IPStmt subStmt in compoundStmt.Statements)
                    {
                        WriteStmt(context, output, subStmt);
                    }

                    context.WriteLine(output, "}");
                    break;
                case CtorStmt ctorStmt:
                    break;
                case FunCallStmt funCallStmt:
                    break;
                case GotoStmt gotoStmt:
                    break;
                case IfStmt ifStmt:
                    context.Write(output, "if (");
                    WriteExpr(context, output, ifStmt.Condition);
                    context.WriteLine(output, ")");
                    WriteStmt(context, output, ifStmt.ThenBranch);
                    if (ifStmt.ElseBranch != null)
                    {
                        context.WriteLine(output, "else");
                        WriteStmt(context, output, ifStmt.ElseBranch);
                    }
                    break;
                case InsertStmt insertStmt:
                    break;
                case MoveAssignStmt moveAssignStmt:
                    WriteLValue(context, output, moveAssignStmt.ToLocation);
                    context.WriteLine(output, $" = {context.Names.GetNameForDecl(moveAssignStmt.FromVariable)};");
                    break;
                case NoStmt _:
                    break;
                case PopStmt popStmt:
                    break;
                case PrintStmt printStmt:
                    context.Write(output, $"runtime.WriteLine(\"{printStmt.Message}\"");
                    foreach (IPExpr printArg in printStmt.Args)
                    {
                        context.Write(output, ", ");
                        WriteExpr(context, output, printArg);
                    }

                    context.WriteLine(output, ");");
                    break;
                case RaiseStmt raiseStmt:
                    break;
                case ReceiveStmt receiveStmt:
                    break;
                case RemoveStmt removeStmt:
                    break;
                case ReturnStmt returnStmt:
                    context.Write(output, "return ");
                    WriteExpr(context, output, returnStmt.ReturnValue);
                    context.WriteLine(output, ";");
                    break;
                case SendStmt sendStmt:
                    break;
                case SwapAssignStmt swapAssignStmt:
                    break;
                case WhileStmt whileStmt:
                    context.Write(output, "while (");
                    WriteExpr(context, output, whileStmt.Condition);
                    context.WriteLine(output, ")");
                    WriteStmt(context, output, whileStmt.Body);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(stmt));
            }
        }

        private void WriteLValue(CompilationContext context, StringWriter output, IPExpr lvalue)
        {
            switch (lvalue)
            {
                case MapAccessExpr mapAccessExpr:
                    context.Write(output, "(");
                    WriteLValue(context, output, mapAccessExpr.MapExpr);
                    context.Write(output, ")[");
                    WriteExpr(context, output, mapAccessExpr.IndexExpr);
                    context.Write(output, "]");
                    break;
                case NamedTupleAccessExpr namedTupleAccessExpr:
                    throw new NotImplementedException();
                case SeqAccessExpr seqAccessExpr:
                    context.Write(output, "(");
                    WriteLValue(context, output, seqAccessExpr.SeqExpr);
                    context.Write(output, ")[");
                    WriteExpr(context, output, seqAccessExpr.IndexExpr);
                    context.Write(output, "]");
                    break;
                case TupleAccessExpr tupleAccessExpr:
                    throw new NotImplementedException();
                case VariableAccessExpr variableAccessExpr:
                    context.Write(output, context.Names.GetNameForDecl(variableAccessExpr.Variable));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(lvalue));
            }
        }

        private void WriteExpr(CompilationContext context, StringWriter output, IPExpr pExpr)
        {
            switch (pExpr)
            {
                case CloneExpr cloneExpr:
                    WriteClone(context, output, cloneExpr.Term);
                    break;
                case BinOpExpr binOpExpr:
                    context.Write(output, "(");
                    WriteExpr(context, output, binOpExpr.Lhs);
                    context.Write(output, $") {BinOpToStr(binOpExpr.Operation)} (");
                    WriteExpr(context, output, binOpExpr.Rhs);
                    context.Write(output, ")");
                    break;
                case BoolLiteralExpr boolLiteralExpr:
                    context.Write(output, boolLiteralExpr.Value ? "true" : "false");
                    break;
                case CastExpr castExpr:
                    throw new NotImplementedException();
                case CoerceExpr coerceExpr:
                    throw new NotImplementedException();
                case ContainsKeyExpr containsKeyExpr:
                    context.Write(output, "(");
                    WriteExpr(context, output, containsKeyExpr.Map);
                    context.Write(output, ").ContainsKey(");
                    WriteExpr(context, output, containsKeyExpr.Key);
                    context.Write(output, ")");
                    break;
                case CtorExpr ctorExpr:
                    break;
                case DefaultExpr defaultExpr:
                    context.Write(output, GetDefaultValue(context, defaultExpr.Type));
                    break;
                case EnumElemRefExpr enumElemRefExpr:
                    EnumElem enumElem = enumElemRefExpr.Value;
                    context.Write(output, $"{context.Names.GetNameForDecl(enumElem.ParentEnum)}.{context.Names.GetNameForDecl(enumElem)}");
                    break;
                case EventRefExpr eventRefExpr:
                    context.Write(output, $"new {context.Names.GetNameForDecl(eventRefExpr.Value)}()");
                    break;
                case FairNondetExpr _:
                    context.Write(output, "this.FairRandom()");
                    break;
                case FloatLiteralExpr floatLiteralExpr:
                    context.Write(output, $"{floatLiteralExpr.Value}");
                    break;
                case FunCallExpr funCallExpr:
                    break;
                case IntLiteralExpr intLiteralExpr:
                    context.Write(output, $"{intLiteralExpr.Value}");
                    break;
                case KeysExpr keysExpr:
                    context.Write(output, "(");
                    WriteExpr(context, output, keysExpr.Expr);
                    context.Write(output, ").Keys.ToList()");
                    break;
                case LinearAccessRefExpr linearAccessRefExpr:
                    string swapKeyword = linearAccessRefExpr.LinearType.Equals(LinearType.Swap) ? "ref " : "";
                    context.Write(output, $"{swapKeyword}{context.Names.GetNameForDecl(linearAccessRefExpr.Variable)}");
                    break;
                case NamedTupleExpr namedTupleExpr:
                    throw new NotImplementedException();
                case NondetExpr _:
                    context.Write(output, "this.Random()");
                    break;
                case NullLiteralExpr _:
                    context.Write(output, "null");
                    break;
                case SizeofExpr sizeofExpr:
                    context.Write(output, "(");
                    WriteExpr(context, output, sizeofExpr.Expr);
                    context.Write(output, ").Count");
                    break;
                case ThisRefExpr _:
                    context.Write(output, "this");
                    break;
                case UnaryOpExpr unaryOpExpr:
                    context.Write(output, $"{UnOpToStr(unaryOpExpr.Operation)}(");
                    WriteExpr(context, output, unaryOpExpr.SubExpr);
                    context.Write(output, ")");
                    break;
                case UnnamedTupleExpr unnamedTupleExpr:
                    throw new NotImplementedException();
                case ValuesExpr valuesExpr:
                    context.Write(output, "(");
                    WriteExpr(context, output, valuesExpr.Expr);
                    context.Write(output, ").Values.ToList()");
                    break;
                case MapAccessExpr _:
                case NamedTupleAccessExpr _:
                case SeqAccessExpr _:
                case TupleAccessExpr _:
                case VariableAccessExpr _:
                    WriteLValue(context, output, pExpr);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(pExpr));
            }
        }

        private void WriteClone(CompilationContext context, StringWriter output, IExprTerm cloneExprTerm)
        {
            if (!(cloneExprTerm is IVariableRef variableRef))
            {
                WriteExpr(context, output, cloneExprTerm);
                return;
            }

            var variable = variableRef.Variable;
            context.Write(output, RenderClone(context, variable.Type, context.Names.GetNameForDecl(variable)));
        }

        private string RenderClone(CompilationContext context, PLanguageType cloneType, string termName)
        {
            switch (cloneType.Canonicalize())
            {
                case SequenceType seq:
                    var elem = context.Names.GetTemporaryName("elem");
                    return $"({termName}).ConvertAll({elem} => {RenderClone(context, seq.ElementType, elem)})";
                case MapType map:
                    var key = context.Names.GetTemporaryName("k");
                    var val = context.Names.GetTemporaryName("v");
                    return $"({termName}).ToDictionary({key} => {RenderClone(context, map.KeyType, key + ".Key")}, {val} => {RenderClone(context, map.ValueType, val + ".Value")})";
                case PrimitiveType type when type.IsSameTypeAs(PrimitiveType.Int):
                    return termName;
                case PrimitiveType type when type.IsSameTypeAs(PrimitiveType.Float):
                    return termName;
                case PrimitiveType type when type.IsSameTypeAs(PrimitiveType.Bool):
                    return termName;
                case PrimitiveType type when type.IsSameTypeAs(PrimitiveType.Machine):
                    return termName;
                case PrimitiveType type when type.IsSameTypeAs(PrimitiveType.Event):
                    return GetDefaultValue(context, type);
                default:
                    throw new NotImplementedException($"Cloning {cloneType.OriginalRepresentation}");
            }
        }

        private string GetDefaultValue(CompilationContext context, PLanguageType returnType)
        {
            switch (returnType.Canonicalize())
            {
                case EnumType enumType:
                    return $"({context.Names.GetNameForDecl(enumType.EnumDecl)})(0)";
                case MapType mapType:
                    return $"new {GetSolidityType(context, mapType)}()";
                case SequenceType sequenceType:
                    return $"new <{GetSolidityType(context, sequenceType)}>()";
                case NamedTupleType _:
                    throw new NotImplementedException();
                case TupleType _:
                    throw new NotImplementedException();
                case PrimitiveType primitiveType when primitiveType.IsSameTypeAs(PrimitiveType.Bool):
                    return "false";
                case PrimitiveType primitiveType when primitiveType.IsSameTypeAs(PrimitiveType.Int):
                    return "0";
                case PrimitiveType primitiveType when primitiveType.IsSameTypeAs(PrimitiveType.Float):
                    return "0.0";
                case PermissionType _:
                case PrimitiveType anyType when anyType.IsSameTypeAs(PrimitiveType.Any):
                case PrimitiveType eventType when eventType.IsSameTypeAs(PrimitiveType.Event):
                case PrimitiveType machineType when machineType.IsSameTypeAs(PrimitiveType.Machine):
                case ForeignType _:
                case BoundedType _:
                    return "null";
                default:
                    throw new ArgumentOutOfRangeException(nameof(returnType));
            }
        }

        private static string UnOpToStr(UnaryOpType operation)
        {
            switch (operation)
            {
                case UnaryOpType.Negate:
                    return "-";
                case UnaryOpType.Not:
                    return "!";
                default:
                    throw new ArgumentOutOfRangeException(nameof(operation), operation, null);
            }
        }

        private static string BinOpToStr(BinOpType binOpType)
        {
            switch (binOpType)
            {
                case BinOpType.Add:
                    return "+";
                case BinOpType.Sub:
                    return "-";
                case BinOpType.Mul:
                    return "*";
                case BinOpType.Div:
                    return "/";
                case BinOpType.Eq:
                    return "==";
                case BinOpType.Neq:
                    return "!=";
                case BinOpType.Lt:
                    return "<";
                case BinOpType.Le:
                    return "<=";
                case BinOpType.Gt:
                    return ">";
                case BinOpType.Ge:
                    return ">=";
                case BinOpType.And:
                    return "&&";
                case BinOpType.Or:
                    return "||";
                default:
                    throw new ArgumentOutOfRangeException(nameof(binOpType), binOpType, null);
            }
        }

        #endregion

        #region misc helper functions

        /// <summary>
        /// Get the name of the state, in a Solidity-supported format
        /// </summary>
        /// <param name="state"></param>
        /// <returns></returns>
        private string GetQualifiedStateName(State state)
        {
            return state.QualifiedName.Replace(".", "_");
        }

        /// <summary>
        /// Adds the default handler for the eTransfer event, which accepts ether.
        /// </summary>
        /// <param name="context"></param>
        /// <param name="output"></param>
        private void AddTransferFunction(CompilationContext context, StringWriter output)
        {
            context.WriteLine(output, $"function Transfer () public payable");
            context.WriteLine(output, "{");
            context.WriteLine(output, "}");
        }

        /// <summary>
        /// Adds a function which can compare two strings in Solidity
        /// </summary>
        /// <param name="context"></param>
        /// <param name="output"></param>
        private void AddStringComparator(CompilationContext context, StringWriter output)
        {
            context.WriteLine(output, $"function CompareStrings (string s1, string s2) view returns (bool)");
            context.WriteLine(output, "{");
            context.WriteLine(output, $"return keccak256(s1) == keccak256(s2);");
            context.WriteLine(output, "}");
        }

        /// <summary>
        /// Build the NextStateMap: Event -> (CurrentState -> NextState)
        /// </summary>
        /// <param name="machine"></param>
        private void BuildNextStateMap(CompilationContext context, Machine machine)
        {
            foreach(State state in machine.States)
            {
                foreach (var eventHandler in state.AllEventHandlers)
                {
                    PEvent pEvent = eventHandler.Key;
                    Dictionary<string, string> pEventStateChanges;

                    int typeId = TypeIdMap[pEvent.Name];

                    // Create an entry for pEvent, if we haven't encountered this before
                    if(! NextStateMap.Keys.Contains(typeId))
                    {
                        NextStateMap.Add(typeId, new Dictionary<string, string>());
                        pEventStateChanges = new Dictionary<string, string>();
                    }
                    else
                    {
                        pEventStateChanges = NextStateMap[typeId];
                    }

                    IStateAction stateAction = eventHandler.Value;

                    switch (stateAction)
                    {
                        case EventGotoState eventGotoState when eventGotoState.TransitionFunction != null:
                            pEventStateChanges.Add(GetQualifiedStateName(state), GetQualifiedStateName(eventGotoState.Target));
                            break;

                        case EventGotoState eventGotoState when eventGotoState.TransitionFunction == null:
                            pEventStateChanges.Add(GetQualifiedStateName(state), GetQualifiedStateName(eventGotoState.Target));
                            break;

                        case EventDoAction eventDoAction:
                            break;

                        default:
                            throw new Exception("BuildNextStateMap: Unsupported/Incorrect event handler specification");
                    }

                    NextStateMap[typeId] = pEventStateChanges;
                }
            }
        }

        /// <summary>
        /// Build the action lookup map: Event -> (CurrentState -> Action)
        /// </summary>
        /// <param name="machine"></param>
        private void BuildActionMap(CompilationContext context, Machine machine)
        {
            foreach (State state in machine.States)
            {
                foreach (var eventHandler in state.AllEventHandlers)
                {
                    PEvent pEvent = eventHandler.Key;
                    Dictionary<string, string> pEventActionForState;

                    int typeId = TypeIdMap[pEvent.Name];

                    // Create an entry for pEvent, if we haven't encountered this before
                    if (! ActionMap.Keys.Contains(typeId))
                    {
                        ActionMap.Add(typeId, new Dictionary<string, string>());
                        pEventActionForState = new Dictionary<string, string>();
                    }
                    else
                    {
                        pEventActionForState = ActionMap[typeId];
                    }

                    IStateAction stateAction = eventHandler.Value;

                    switch (stateAction)
                    {
                        case EventGotoState eventGotoState when eventGotoState.TransitionFunction != null:
                            pEventActionForState.Add(GetQualifiedStateName(state), eventGotoState.TransitionFunction.Name);
                            break;

                        case EventGotoState eventGotoState when eventGotoState.TransitionFunction == null:
                            break;

                        case EventDoAction eventDoAction:
                            pEventActionForState.Add(GetQualifiedStateName(state), context.Names.GetNameForDecl(eventDoAction.Target));
                            break;

                        default:
                            throw new Exception("BuildActionMap: Unsupported/Incorrect event handler specification");
                    }

                    ActionMap[typeId] = pEventActionForState;
                }
            }
        }

        #endregion

    }
}
