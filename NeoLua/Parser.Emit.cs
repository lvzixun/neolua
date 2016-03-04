﻿using System;
using System.Dynamic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace Neo.IronLua
{
	#region -- enum ReflectionFlag ------------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	internal enum ReflectionFlag
	{
		None = 0,
		Static = 1,
		Instance = 2,
		NonPublic = 4,
		Public = 8,
		IgnoreCase = 16,
		NoException = 32,
		NoArguments = 64
	} //enum ReflectionFlag

	#endregion

	#region -- class Parser -------------------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	internal static partial class Parser
	{
		private static readonly Func<Expression, Expression> getExpressionFunction = GetExpression;
		private static readonly Func<Expression, Type> getExpressionTypeFunction = GetExpressionType;

		#region -- Debug-Information ------------------------------------------------------

		private static Expression WrapDebugInfo(bool lWrap, bool lAfter, Token tStart, Token tEnd, Expression expr)
		{
			if (lWrap)
			{
				if (lAfter)
				{
					if (expr.Type == typeof(void))
						return Expression.Block(expr, GetDebugInfo(tStart, tEnd));
					else
					{
						ParameterExpression r = Expression.Variable(expr.Type);
						return Expression.Block(r.Type, new ParameterExpression[] { r },
							Expression.Assign(r, expr),
							GetDebugInfo(tStart, tEnd),
							r);
					}
				}
				else
					return Expression.Block(GetDebugInfo(tStart, tEnd), expr);
			}
			else
				return expr;
		} // func WrapDebugInfo

		private static Expression GetDebugInfo(Token tStart, Token tEnd)
		{
			return Expression.DebugInfo(tStart.Start.Document, tStart.Start.Line, tStart.Start.Col, tEnd.End.Line, tEnd.End.Col);
		} // func GetDebugInfo

		#endregion

		#region -- Emit Helper ------------------------------------------------------------

		private static Expression SafeExpression(Func<Expression> f, Token tokenStart)
		{
			try
			{
				return f();
			}
			catch (LuaEmitException e)
			{
				throw ParseError(tokenStart, e.Message);
			}
		} // func SafeExpression

		private static Expression ConvertExpressionNoneEmit(Lua runtime, Token tokenStart, Expression expr, Type toType, bool forceType)
		{
			if (expr.Type == typeof(LuaResult) && toType != typeof(LuaResult)) // shortcut for LuaResult ==> expr[0]
			{
				if (expr.NodeType == ExpressionType.New) // new LuaResult(?)
				{
					var newExpression = (NewExpression)expr;
					if (newExpression.Constructor == Lua.ResultConstructorInfoArg1 || newExpression.Constructor == Lua.ResultConstructorInfoArgN)
						return ConvertExpressionNoneEmit(runtime, tokenStart, newExpression.Arguments.First(), toType, forceType);
				}
				else if (expr.NodeType == ExpressionType.Dynamic) // (LuaResult)?
				{
					var dynamicExpression = (DynamicExpression)expr;
					if (dynamicExpression.Binder is ConvertBinder)
						return ConvertExpressionNoneEmit(runtime, tokenStart, DynamicExpression.Dynamic(runtime.GetConvertBinder(toType), toType, dynamicExpression.Arguments.First()), toType, forceType);
				}

				return GetResultExpression(runtime, tokenStart, expr, 0); // is forced by default
			}
			else if (expr.Type == typeof(object) && expr.NodeType == ExpressionType.Dynamic) // wrap dynamic Invokes
			{
				var exprDynamic = (DynamicExpression)expr;
				if (exprDynamic.Binder is InvokeBinder || exprDynamic.Binder is InvokeMemberBinder) // convert the result of a invoke to object
					return ConvertExpressionNoneEmit(runtime, tokenStart, DynamicExpression.Dynamic(runtime.GetConvertBinder(toType), toType, expr), toType, forceType);
				else if (exprDynamic.Binder is ConvertBinder && exprDynamic.Type != toType)
					return ConvertExpressionNoneEmit(runtime, tokenStart, DynamicExpression.Dynamic(runtime.GetConvertBinder(toType), toType, exprDynamic.Arguments.First()), toType, forceType);

				// fall to forceType
			}

			if (forceType) // force
				return Lua.EnsureType(expr, typeof(object));
			else
				return expr;
		} // func ConvertExpressionNoneEmit

		private static Expression ConvertObjectExpression(Lua runtime, Token tokenStart, Expression expr, bool convertToObject = false)
		{
			return ConvertExpressionNoneEmit(runtime, tokenStart, expr, typeof(object), convertToObject);
		} // func ConvertObjectExpression

		private static Expression ConvertExpression(Lua runtime, Token tokenStart, Expression expr, Type toType)
		{
			// convert from multi result to single result
			expr = ConvertExpressionNoneEmit(runtime, tokenStart, expr, toType, false);

			// dynamic convert
			object result;
			if (LuaEmit.TryConvertCore(expr, expr.Type, toType, runtime.GetConvertBinder, out result))
				return (Expression)result;
			else
				throw ParseError(tokenStart, ((LuaEmitException)result).Message);
		} // func ConvertExpression

		private static Expression GetResultExpression(Lua runtime, Token tStart, Expression expr, int iIndex)
		{
			return SafeExpression(() => LuaEmit.GetResultExpression(expr, iIndex), tStart);
		} // func GetResultExpression

		private static Expression UnaryOperationExpression(Lua runtime, Token tStart, ExpressionType op, Expression expr)
		{
			if (op != ExpressionType.ArrayLength)
				expr = ConvertObjectExpression(runtime, tStart, expr);
			return SafeExpression(() => LuaEmit.UnaryOperationExpression(runtime, op, expr, expr.Type, true), tStart);
		} // func UnaryOperationExpression

		private static Expression BinaryOperationExpression(Lua runtime, Token tStart, ExpressionType op, Expression expr1, Expression expr2)
		{
			expr1 = ConvertObjectExpression(runtime, tStart, expr1);
			expr2 = ConvertObjectExpression(runtime, tStart, expr2);
			return SafeExpression(() => LuaEmit.BinaryOperationExpression(runtime, op, expr1, expr1.Type, expr2, expr2.Type, true), tStart);
		} // func BinaryOperationExpression

		private static Expression ConcatOperationExpression(Lua runtime, Token tStart, Expression[] args)
		{
			if (Array.Exists(args, c => LuaEmit.IsDynamicType(c.Type))) // we have a dynamic type in the list -> to the concat on runtime
			{
				return SafeExpression(() => Expression.Call(Lua.ConcatStringMethodInfo, Expression.NewArrayInit(typeof(object),
					from e in args select ConvertObjectExpression(runtime, tStart, e, true))), tStart);
			}
			else
			{
				return SafeExpression(() => Expression.Call(Lua.StringConcatMethodInfo, Expression.NewArrayInit(typeof(string),
					from e in args select ConvertExpression(runtime, tStart, e, typeof(string)))), tStart);
			}
		} // func ConcatOperationExpression

		private static Expression MemberGetSandbox(Scope scope, Expression getMember, Expression instance, string sMember)
		{
			return scope.Options.SandboxCore(getMember, instance, sMember);
		} // func MemberGetSandbox

		private static Expression MemberGetExpressionCore(Lua lua, Token tokenStart, Expression instance, string memberName)
		{
			// if this is a dynamic type, let the type deside what is to do
			if (LuaEmit.IsDynamicType(instance.Type))
				return DynamicExpression.Dynamic(lua.GetGetMemberBinder(memberName), typeof(object), ConvertObjectExpression(lua, tokenStart, instance, true));
			else
			{
				Expression result;
				switch (LuaEmit.TryGetMember(instance, instance.Type, memberName, false, out result))
				{
					case LuaTryGetMemberReturn.None:
						throw ParseError(tokenStart, LuaEmitException.GetMessageText(LuaEmitException.MemberNotFound, instance.Type.Name, memberName));
					case LuaTryGetMemberReturn.NotReadable:
						throw ParseError(tokenStart, LuaEmitException.GetMessageText(LuaEmitException.CanNotReadMember, instance.Type.Name, memberName));
					case LuaTryGetMemberReturn.ValidExpression:
						return result;
					default:
						throw new ArgumentException("Internal return type of TryGetMember");
				}
			}
		} // func MemberGetExpressionCore

		private static Expression MemberGetExpression(Scope scope, Token tokenStart, Expression instance, string memberName)
			=> MemberGetSandbox(scope, MemberGetExpressionCore(scope.Runtime, tokenStart, instance, memberName), instance, memberName);

		private static Expression MemberSetExpressionCore(Lua lua, Token tokenStart, Expression instance, string memberName, Expression set)
		{
			if (LuaEmit.IsDynamicType(instance.Type)) // first call the dynamic interface
			{
				return DynamicExpression.Dynamic(lua.GetSetMemberBinder(memberName), typeof(object),
					ConvertObjectExpression(lua, tokenStart, instance, true),
					ConvertObjectExpression(lua, tokenStart, set, true)
				);
			}
			else
			{
				Expression result;
				switch (LuaEmit.TrySetMember(instance, instance.Type, memberName, false, (setType) => ConvertExpression(lua, tokenStart, set, setType), out result))
				{
					case LuaTrySetMemberReturn.None:
						throw ParseError(tokenStart, LuaEmitException.GetMessageText(LuaEmitException.MemberNotFound, instance.Type.Name, memberName));
					case LuaTrySetMemberReturn.NotWritable:
						throw ParseError(tokenStart, LuaEmitException.GetMessageText(LuaEmitException.CanNotWriteMember, instance.Type.Name, memberName));
					case LuaTrySetMemberReturn.ValidExpression:
						return result;
					default:
						throw new ArgumentException("Internal return type of TrySetMember");
				}
			}
		} // func MemberSetExpressionCore

		private static Expression MemberSetExpression(Lua runtime, Token tokenStart, Expression instance, string memberName, bool setMethodMember, Expression set)
		{
			// Assign the value to a member
			if (setMethodMember)
			{
				return Expression.Call(
					 ConvertExpression(runtime, tokenStart, instance, typeof(LuaTable)),
					 Lua.TableDefineMethodLightMethodInfo,
					 Expression.Constant(memberName, typeof(string)),
					 ConvertExpression(runtime, tokenStart, set, typeof(Delegate))
				);
			}
			else
				return SafeExpression(() => MemberSetExpressionCore(runtime, tokenStart, instance, memberName, set), tokenStart);
		} // func MemberSetExpression

		private static Expression IndexGetExpression(Scope scope, Token tStart, Expression instance, Expression[] indexes)
		{
			if (instance.Type == typeof(LuaTable))
			{
				if (indexes.Length == 1)
				{
					var arg = indexes[0];

					if (LuaTable.IsIndexKey(arg.Type)) // integer access
					{
						return MemberGetSandbox(
							scope,
							Expression.Call(instance, Lua.TableGetValueKeyIntMethodInfo,
								ConvertExpression(scope.Runtime, tStart, arg, typeof(int)),
								Expression.Constant(false)
							),
							instance, null
						);
					}
					else if (arg.Type == typeof(string)) // member access
					{
						return MemberGetSandbox(
							scope,
							Expression.Call(instance, Lua.TableGetValueKeyStringMethodInfo,
								arg,
								Expression.Constant(false),
								Expression.Constant(false)
							),
							instance, null
						);
					}
					else // key access
					{
						return MemberGetSandbox(
							scope,
							Expression.Call(instance, Lua.TableGetValueKeyObjectMethodInfo,
								ConvertObjectExpression(scope.Runtime, tStart, arg, true),
								Expression.Constant(false)
							),
							instance, null
						);
					}
				}
				else
				{
					return MemberGetSandbox(
						scope,
						Expression.Call(instance, Lua.TableGetValueKeyListMethodInfo,
							Expression.NewArrayInit(typeof(object), from i in indexes select ConvertObjectExpression(scope.Runtime, tStart, i, true)),
							Expression.Constant(false)
						),
						instance, null
					);
				}
			}
			else if (instance.Type == typeof(LuaResult) && indexes.Length == 1)
			{
				return MemberGetSandbox(
					scope,
					Expression.MakeIndex(
						instance,
						Lua.ResultIndexPropertyInfo,
						new Expression[] { ConvertExpression(scope.Runtime, tStart, indexes[0], typeof(int)) }
					),
					instance, null
				);
			}
			else
				return MemberGetSandbox(scope, SafeExpression(() => LuaEmit.GetIndex(scope.Runtime, instance, indexes, getExpressionFunction, getExpressionTypeFunction, true), tStart), instance, null);
		} // func IndexGetExpression

		private static Expression IndexSetExpression(Lua runtime, Token tStart, Expression instance, Expression[] indexes, Expression set)
		{
			if (instance.Type == typeof(LuaTable))
			{
				if (indexes.Length == 1)
				{
					var arg = indexes[0];
					if (LuaTable.IsIndexKey(arg.Type)) // integer access
					{
						return Expression.Call(instance, Lua.TableSetValueKeyIntMethodInfo,
							 ConvertExpression(runtime, tStart, arg, typeof(int)),
							 ConvertObjectExpression(runtime, tStart, set, true),
							 Expression.Constant(false)
						 );
					}
					else if (arg.Type == typeof(string)) // member access
					{
						return Expression.Call(instance, Lua.TableSetValueKeyStringMethodInfo,
							 arg,
							 ConvertObjectExpression(runtime, tStart, set, true),
							 Expression.Constant(false),
							 Expression.Constant(false)
						 );
					}
					else // key access
					{
						return Expression.Call(instance, Lua.TableSetValueKeyObjectMethodInfo,
							 ConvertObjectExpression(runtime, tStart, arg, true),
							 ConvertObjectExpression(runtime, tStart, set, true),
							 Expression.Constant(false)
						 );
					}
				}
				else
				{
					return Expression.Call(instance, Lua.TableSetValueKeyListMethodInfo,
						Expression.NewArrayInit(typeof(object), from i in indexes select ConvertObjectExpression(runtime, tStart, i, true)),
						ConvertObjectExpression(runtime, tStart, set, true),
						Expression.Constant(false)
					);
				}
			}
			else
				return SafeExpression(() => LuaEmit.SetIndex(runtime, instance, indexes, set, getExpressionFunction, getExpressionTypeFunction, true), tStart);
		} // func IndexSetExpression

		private static Expression InvokeExpression(Scope scope, Token tStart, Expression instance, InvokeResult result, ArgumentsList arguments, bool lParse)
		{
			MethodInfo mi;
			ConstantExpression constInstance = instance as ConstantExpression;
			LuaType t;
			if (constInstance != null && (t = constInstance.Value as LuaType) != null && t.Type != null) // we have a type, bind the ctor
			{
				var type = t.Type;
				var typeInfo = type.GetTypeInfo();
				var ci =
					typeInfo.IsValueType && arguments.Count == 0 ?
						null :
						LuaEmit.FindMember(typeInfo.DeclaredConstructors.Where(c => c.IsPublic), arguments.CallInfo, arguments.Expressions, getExpressionTypeFunction, false);

				if (ci == null && !typeInfo.IsValueType)
					throw ParseError(tStart, String.Format(Properties.Resources.rsMemberNotResolved, type.Name, "ctor"));

				return SafeExpression(() => LuaEmit.BindParameter(scope.Runtime,
					args => ci == null ? Expression.New(type) : Expression.New(ci, args),
					ci == null ? new ParameterInfo[0] : ci.GetParameters(),
					arguments.CallInfo,
					arguments.Expressions,
					getExpressionFunction, getExpressionTypeFunction, true), tStart);
			}
			else if (LuaEmit.IsDynamicType(instance.Type))
			{
				// fallback is a dynamic call
				return EnsureInvokeResult(scope, tStart,
					DynamicExpression.Dynamic(scope.Runtime.GetInvokeBinder(arguments.CallInfo),
						typeof(object),
						new Expression[] { ConvertExpression(scope.Runtime, tStart, instance, typeof(object)) }.Concat(
							from c in arguments.Expressions select Lua.EnsureType(c, typeof(object))
						)
					),
					result, instance, null
				);
			}
			else if (typeof(Delegate).GetTypeInfo().IsAssignableFrom(instance.Type.GetTypeInfo()) &&  // test if the type is assignable from delegate
				(mi = instance.Type.GetRuntimeMethods().Where(c => !c.IsStatic && c.IsPublic && c.Name == "Invoke").FirstOrDefault()) != null) // Search the Invoke method for the arguments
			{
				return EnsureInvokeResult(scope, tStart,
					SafeExpression(() => LuaEmit.BindParameter<Expression>(
						scope.Runtime,
						args => Expression.Invoke(instance, args),
						mi.GetParameters(),
						arguments.CallInfo,
						arguments.Expressions,
						getExpressionFunction, getExpressionTypeFunction, true), tStart),
					result, instance, null
				);
			}
			else
				throw ParseError(tStart, LuaEmitException.GetMessageText(LuaEmitException.InvokeNoDelegate, instance.Type.Name));
		}  // func InvokeExpression

		private static Expression InvokeMemberExpression(Scope scope, Token tStart, Expression instance, string sMember, InvokeResult result, ArgumentsList arguments)
		{
			if (LuaEmit.IsDynamicType(instance.Type))
			{
				return EnsureInvokeResult(scope, tStart,
					DynamicExpression.Dynamic(scope.Runtime.GetInvokeMemberBinder(sMember, arguments.CallInfo), typeof(object),
						new Expression[] { ConvertExpression(scope.Runtime, tStart, instance, typeof(object)) }.Concat(
							from c in arguments.Expressions select Lua.EnsureType(c, typeof(object))
						).ToArray()
					),
					result, instance, sMember
				 );
			}
			else
			{
				// look up the method
				MethodInfo method = LuaEmit.FindMethod(
					LuaType.GetType(instance.Type).EnumerateMembers<MethodInfo>(LuaMethodEnumerate.Typed, sMember, false),
					arguments.CallInfo,
					instance,
					arguments.Expressions,
					getExpressionTypeFunction,
					true);

				if (method != null)
					return EnsureInvokeResult(scope, tStart, SafeExpression(() => InvokeMemberExpressionBind(method, scope.Runtime, instance, arguments), tStart), result, instance, sMember);
				else
					return InvokeMemberExpressionDynamic(scope, tStart, instance, sMember, result, arguments);
			}
		} // func InvokeMemberExpression

		private static Expression InvokeMemberExpressionBind(MethodInfo method, Lua runtime, Expression instance, ArgumentsList arguments)
		{
			if (method.IsStatic)
			{
				return LuaEmit.BindParameter(runtime,
					args => Expression.Call(null, method, args),
					method.GetParameters(),
					arguments.CallInfo,
					new Expression[] { instance }.Concat(
						from a in arguments.Expressions select Lua.EnsureType(a, typeof(object))
					).ToArray(),
					getExpressionFunction, getExpressionTypeFunction, true);
			}
			else
			{
				return LuaEmit.BindParameter(runtime,
					args => Expression.Call(instance, method, args),
					method.GetParameters(),
					arguments.CallInfo,
					arguments.Expressions,
					getExpressionFunction, getExpressionTypeFunction, true);
			}
		} // func InvokeMemberExpressionBind

		private static Expression InvokeMemberExpressionDynamic(Scope scope, Token tStart, Expression instance, string sMember, InvokeResult result, ArgumentsList arguments)
		{
			return EnsureInvokeResult(scope, tStart,
				DynamicExpression.Dynamic(scope.Runtime.GetInvokeMemberBinder(sMember, arguments.CallInfo), typeof(object),
					new Expression[] { ConvertExpression(scope.Runtime, tStart, instance, typeof(object)) }.Concat(from a in arguments.Expressions select Lua.EnsureType(a, typeof(object))).ToArray()
				),
				result, instance, sMember
			 );
		} // func InvokeMemberExpressionDynamic

		private static Expression EnsureInvokeResult(Scope scope, Token tStart, Expression expr, InvokeResult result, Expression instance, string memberName)
		{
			switch (result)
			{
				case InvokeResult.LuaResult:
					if (expr.Type == typeof(object))
						return ConvertExpression(scope.Runtime, tStart, expr, typeof(LuaResult));
					else
						return MemberGetSandbox(scope, expr, instance, memberName);
				case InvokeResult.Object:
					if (LuaEmit.IsDynamicType(expr.Type))
						return MemberGetSandbox(scope, DynamicExpression.Dynamic(scope.Runtime.GetConvertBinder(typeof(object)), typeof(object), ConvertExpression(scope.Runtime, tStart, expr, typeof(object))), instance, memberName);
					else
						return MemberGetSandbox(scope, expr, instance, memberName);
				default:
					return MemberGetSandbox(scope, expr, instance, memberName);
			}
		} // func EnsureInvokeResult

		#endregion

		internal static Type GetDelegateType(MethodInfo mi)
		{
			return Expression.GetDelegateType(
				(
					from p in mi.GetParameters()
					select p.ParameterType
				).Concat(
					new Type[] { mi.ReturnType }
				).ToArray()
			);
		} // func GetDelegateType

		internal static Delegate CreateDelegate(object firstArgument, MethodInfo mi)
		{
			if ((mi.CallingConvention & CallingConventions.VarArgs) != 0)
				throw new ArgumentException("Call of VarArgs not implemented.");

			return mi.CreateDelegate(GetDelegateType(mi), firstArgument);
		} // func CreateDelegateFromMethodInfo

		private static Type GetExpressionType(Expression e)
		{
			return e.Type;
		} // func GetExpressionType

		private static Expression GetExpression(Expression e)
		{
			return e;
		} // func GetExpression
	} // class Parser

	#endregion
}
