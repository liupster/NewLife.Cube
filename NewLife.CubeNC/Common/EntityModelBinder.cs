﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Binders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NewLife.Log;
using NewLife.Reflection;
using NewLife.Serialization;
using XCode;

namespace NewLife.Cube
{
    /// <summary>实体模型绑定器</summary>
    class EntityModelBinder : ComplexTypeModelBinder
    {
        /// <summary>实例化实体模型绑定器</summary>
        /// <param name="propertyBinders"></param>
        /// <param name="loggerFactory"></param>
        public EntityModelBinder(IDictionary<ModelMetadata, IModelBinder> propertyBinders, ILoggerFactory loggerFactory)
            : base(propertyBinders, loggerFactory) { }

        /// <summary>创建模型。对于有Key的请求，使用FindByKeyForEdit方法先查出来数据，而不是直接反射实例化实体对象</summary>
        /// <param name="bindingContext"></param>
        /// <returns></returns>
        protected override Object CreateModel(ModelBindingContext bindingContext)
        {
            var modelType = bindingContext.ModelType;
            if (modelType.As<IEntity>())
            {
                var fact = EntityFactory.CreateOperate(modelType);
                if (fact != null)
                {
                    var rvs = bindingContext.ActionContext.RouteData.Values;
                    var pks = fact.Table.PrimaryKeys;
                    var uk = fact.Unique;

                    IEntity entity = null;
                    if (uk != null)
                    {
                        // 查询实体对象用于编辑
                        var id = rvs[uk.Name];
                        if (id != null) entity = fact.FindByKeyForEdit(id);
                        if (entity == null) entity = fact.Create(true);
                    }
                    else if (pks.Length > 0)
                    {
                        // 查询实体对象用于编辑
                        var req = bindingContext.HttpContext.Request.Query;
                        var exp = new WhereExpression();
                        foreach (var item in pks)
                        {
                            var v = req[item.Name].FirstOrDefault();
                            exp &= item.Equal(v.ChangeType(item.Type));
                        }

                        entity = fact.Find(exp);

                        if (entity == null) entity = fact.Create(true);
                    }

                    // 尝试从body读取json格式的参数
                    var request = bindingContext.HttpContext.Request;
                    if (request.ContentType.Contains("json") && request.ContentLength > 0)
                    {
                        // 允许同步IO
                        var ft = bindingContext.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpBodyControlFeature>();
                        if (ft != null) ft.AllowSynchronousIO = true;

                        var body = request.Body.ToStr();
                        var entityBody = body.ToJsonEntity(typeof(Object)); // NullableDictionary<string,object>)
                        bindingContext.HttpContext.Items["EntityBody"] = entityBody;
                    }

                    return entity ?? fact.Create(true);
                }
            }

            return base.CreateModel(bindingContext);
        }

        protected override Boolean CanBindProperty(ModelBindingContext bindingContext, ModelMetadata propertyMetadata)
        {
            // 不要绑定复杂类型，那是扩展属性
            if (propertyMetadata.ModelType.GetTypeCode() == TypeCode.Object) return false;

            return base.CanBindProperty(bindingContext, propertyMetadata);
        }

        /// <summary>
        /// 绑定属性，在这里赋值
        /// </summary>
        /// <param name="bindingContext"></param>
        /// <returns></returns>
        protected override Task BindProperty(ModelBindingContext bindingContext)
        {
            var metadata = bindingContext.ModelMetadata;
            var result = bindingContext.Result;
            switch (metadata.ModelType.GetTypeCode())
            {
                case TypeCode.DateTime:
                    // 客户端可能提交空时间，不要绑定属性，以免出现空时间验证失败
                    //if (result.Model is not DateTime) return Task.CompletedTask;
                    var dt = bindingContext.HttpContext.Request.Form[metadata.Name];
                    if (dt.Count == 0 || dt.ToString().IsNullOrEmpty()) return Task.CompletedTask;

                    break;
            }

            Object val;
            var entityBody = bindingContext.HttpContext.Items["EntityBody"] as NewLife.Collections.NullableDictionary<String, Object>;
            var fieldName = bindingContext.FieldName;
            if (entityBody != null && (val = entityBody[fieldName]) != null)
            {
                bindingContext.Result = ModelBindingResult.Success(val);
                return Task.CompletedTask;
            }

            return base.BindProperty(bindingContext);
        }

        /// <summary>
        /// 设置属性，二次处理
        /// </summary>
        /// <param name="bindingContext"></param>
        /// <param name="modelName"></param>
        /// <param name="propertyMetadata"></param>
        /// <param name="result"></param>
        protected override void SetProperty(ModelBindingContext bindingContext, String modelName, ModelMetadata propertyMetadata, ModelBindingResult result)
        {
            switch (propertyMetadata.ModelType.GetTypeCode())
            {
                case TypeCode.String:
                    // 如果有多个值，则修改结果，避免 3,2,5 变成只有3
                    var vs = bindingContext.ValueProvider.GetValue(modelName).Values;
                    if (vs.Count > 1) result = ModelBindingResult.Success(vs.ToString());
                    break;
            }

            base.SetProperty(bindingContext, modelName, propertyMetadata, result);
        }
    }

    /// <summary>实体模型绑定器提供者，为所有XCode实体类提供实体模型绑定器</summary>
    public class EntityModelBinderProvider : IModelBinderProvider
    {
        /// <summary>获取绑定器</summary>
        /// <param name="context"></param>
        /// <returns></returns>
        public IModelBinder GetBinder(ModelBinderProviderContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            if (!context.Metadata.ModelType.As<IEntity>()) return null;

            var loggerFactory = context.Services.GetRequiredService<ILoggerFactory>();
            var propertyBinders = new Dictionary<ModelMetadata, IModelBinder>();
            foreach (var property in context.Metadata.Properties)
            {
                propertyBinders.Add(property, context.CreateBinder(property));
            }

            return new EntityModelBinder(propertyBinders, loggerFactory);
        }

        /// <summary>实例化</summary>
        public EntityModelBinderProvider() => XTrace.WriteLine("注册实体模型绑定器：{0}", typeof(EntityModelBinderProvider).FullName);
    }
}