using Microsoft.CodeAnalysis;
using Subro.Generators;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Viaduct.Generation;
using Viaduct.Generation.Client;

namespace Viaduct.Tests
{
    partial class GeneratorTestsBase
    {
        // Reusable type references
        static readonly TypeReference StringType = new("String", "string");
        static readonly TypeReference IntType = new("Int32", "int");
        static readonly TypeReference BoolType = new("Boolean", "bool");
        static readonly TypeReference VoidType = new("Void", "void");
        static readonly TypeReference TaskType = new("Task", "System.Threading.Tasks.Task");
        static readonly TypeReference TaskOfString = new("Task", "System.Threading.Tasks.Task<string>");
        static readonly TypeReference TaskOfInt = new("Task", "System.Threading.Tasks.Task<int>");
        static readonly TypeReference UserDtoType = new("UserDto", "TestApp.UserDto");
        static readonly TypeReference TaskOfUserDto = new("Task", "System.Threading.Tasks.Task<TestApp.UserDto>");
        static readonly TypeReference ListOfUserDto = new("List", "System.Collections.Generic.List<TestApp.UserDto>");
        static readonly TypeReference TaskOfListOfUserDto = new("Task", "System.Threading.Tasks.Task<System.Collections.Generic.List<TestApp.UserDto>>");



        /// <summary>
        /// Helper: create a simple route-bindable parameter (string/int/bool).
        /// </summary>
        static ParameterCreationInfo SimplePar(string name, TypeReference type, int index, bool optional = false)
            => new(name, type, index,
                   PredefinedTemplateId: null,
                   IsSimpleType: true,
                   QueryParameter: true,
                   IsOptional: optional);

        /// <summary>
        /// Helper: create a body parameter (complex type, not route-bindable).
        /// </summary>
        static ParameterCreationInfo BodyPar(string name, TypeReference type, int index)
            => new(name, type, index,
                   PredefinedTemplateId: null,
                   IsSimpleType: false,
                   QueryParameter: false,
                   IsOptional: false);

        // ─── Scenario builders ─────────────────────────────────────────────

        internal static InterfaceMetaData BuildMixedInterface()
        {
            var idParam = SimplePar("id", IntType, 0);
            var bodyParam = BodyPar("user", UserDtoType, 0);
            var updateId = SimplePar("id", IntType, 0);
            var updateBody = BodyPar("user", UserDtoType, 1);
            var queryParam = SimplePar("query", StringType, 0);
            var pageParam = SimplePar("page", IntType, 1);
            var includeParam = SimplePar("includeInactive", BoolType, 2);
            var importBody = BodyPar("users", ListOfUserDto, 0);

            var meta = new InterfaceMetaData("IUserApi", "TestApp", "/api/users",
            // 1) async Task<List<UserDto>> GetAll() — no params, async, returns value
            [new MethodCreationInfo(
                "GetAll", "GetAll", "GET",
                Parameters: [],
                Body: null, CancellationToken: null,
                ReturnType: TaskOfListOfUserDto,
                IsAsync: true, ReturnsValue: true,
                ListOfUserDto),

            // 2) async Task<UserDto> GetById(int id) — one simple param, async, returns value
           
            new MethodCreationInfo(
                "GetById", "GetById", "GET",
                Parameters: [idParam],
                Body: null, CancellationToken: null,
                ReturnType: TaskOfUserDto,
                IsAsync: true, ReturnsValue: true,
               UserDtoType),

            // 3) async Task CreateUser(UserDto user) — body param, async, no return value
           
           new MethodCreationInfo(
                "CreateUser", "CreateUser", "POST",
                Parameters: [bodyParam],
                Body: bodyParam, CancellationToken: null,
                ReturnType: TaskType,
                IsAsync: true, ReturnsValue: false,
                null),

            // 4) async Task UpdateUser(int id, UserDto user) — mixed params + body, async, no return value
   
            new MethodCreationInfo(
                "UpdateUser", "UpdateUser", "PUT",
                Parameters: [updateId, updateBody],
                Body: updateBody, CancellationToken: null,
                ReturnType: TaskType,
                IsAsync: true, ReturnsValue: false,
                null),

            // 5) void DeleteUser(int id) — sync void, one simple param
 
           new MethodCreationInfo(
                "DeleteUser", "DeleteUser", "DELETE",
                Parameters: [idParam],
                Body: null, CancellationToken: null,
                ReturnType: VoidType,
                IsAsync: false, ReturnsValue: false,
                null),

            // 6) int GetCount() — sync, returns value, no params
            new MethodCreationInfo(
                "GetCount", "GetCount", "GET",
                Parameters: [],
                Body: null, CancellationToken: null,
                ReturnType: IntType,
                IsAsync: false, ReturnsValue: true,
                null),

            // 7) async Task<string> Search(string query, int page, bool includeInactive)
            //    — multiple simple params, async, returns value
    
            new MethodCreationInfo(
                "Search", "Search", "GET",
                Parameters: [queryParam, pageParam, includeParam],
                Body: null, CancellationToken: null,
                ReturnType: TaskOfString,
                IsAsync: true, ReturnsValue: true,
                StringType),

            // 8) async Task<int> ImportUsers(List<UserDto> users) — body only, async, returns value
          
            new MethodCreationInfo(
                "ImportUsers", "ImportUsers", "POST",
                Parameters: [importBody],
                Body: importBody, CancellationToken: null,
                ReturnType: TaskOfInt,
                IsAsync: true, ReturnsValue: true,
                IntType)]);

            return meta;
        }
    }
}
