// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Microsoft.Framework.Internal;

namespace Microsoft.AspNet.Mvc.ModelBinding.Validation
{
    public class ValidationVisitor
    {
        private readonly IModelMetadataProvider _metadataProvider;
        private readonly IModelValidatorProvider _validatorProvider;
        private readonly IList<IExcludeTypeValidationFilter> _excludeFilters;
        private readonly ModelStateDictionary _modelState;
        private readonly ValidationStateDictionary _validationState;

        private object _container;
        private string _key;
        private object _model;
        private ModelMetadata _metadata;

        private HashSet<object> _currentPath;

        public ValidationVisitor(
            [NotNull] IModelMetadataProvider metadataProvider,
            [NotNull] IModelValidatorProvider validatorProvider,
            [NotNull] IList<IExcludeTypeValidationFilter> excludeFilters,
            [NotNull] ModelStateDictionary modelState,
            [NotNull] ValidationStateDictionary validationState,
            object model)
        {
            _metadataProvider = metadataProvider;
            _validatorProvider = validatorProvider;
            _excludeFilters = excludeFilters;
            _modelState = modelState;
            _validationState = validationState;
            _model = model;

            _currentPath = new HashSet<object>(ReferenceEqualityComparer.Instance);
        }

        public bool Validate()
        {
            ValidationState entry;
            _validationState.TryGetValue(_model, out entry);

            var key = entry?.Key ?? string.Empty;
            var metadata = entry?.Metadata ?? _metadataProvider.GetMetadataForType(_model.GetType());

            if ((entry != null && entry.SuppressValidation) || _modelState.HasReachedMaxErrors)
            {
                SuppressValidation(key);
                return false;
            }
            else
            {
                return Visit(key, metadata, _model);
            }
        }

        protected virtual bool ValidateNode()
        {
            var validators = GetValidators(_metadata);

            var count = validators.Count;
            if (count > 0)
            {
                var context = new ModelValidationContext()
                {
                    Container = _container,
                    Model = _model,
                    Metadata = _metadata,
                };

                var results = new List<ModelValidationResult>();
                for (var i = 0; i < count; i++)
                {
                    results.AddRange(validators[i].Validate(context));
                }

                var resultsCount = results.Count;
                for (var i = 0; i < resultsCount; i++)
                {
                    var result = results[i];
                    var key = ModelNames.CreatePropertyModelName(_key, result.MemberName);
                    _modelState.TryAddModelError(key, result.Message);
                }
            }

            var state = _modelState.GetFieldValidationState(_key);
            if (state == ModelValidationState.Invalid)
            {
                return false;
            }
            else
            {
                // If the field has an entry in ModelState, then record it as valid. Don't create
                // extra entries if they don't exist already.
                var entry = _modelState[_key];
                if (entry != null)
                {
                    entry.ValidationState = ModelValidationState.Valid;
                }

                return true;
            }
        }

        private bool Visit(string key, ModelMetadata metadata, object model)
        {
            RuntimeHelpers.EnsureSufficientExecutionStack();

            if (model != null && !_currentPath.Add(model))
            {
                // This is a cycle, bail.
                return true;
            }

            using (Recursifier.DoTheNeedful(this, key, metadata, model))
            {
                if (_metadata.IsCollectionType)
                {
                    return VisitCollectionType();
                }
                else if (_metadata.IsComplexType)
                {
                    return VisitComplexType();
                }
                else
                {
                    return VisitSimpleType();
                }
            }
        }

        private bool VisitCollectionType()
        {
            var isValid = true;

            if (_model != null)
            {
                var enumerator = ((IEnumerable)_model).GetEnumerator();
                for (var i = 0; enumerator.MoveNext(); i++)
                {
                    var model = enumerator.Current;

                    ValidationState entry;
                    if (model == null)
                    {
                        entry = null;
                    }
                    else
                    {
                        _validationState.TryGetValue(model, out entry);
                    }

                    var key = entry?.Key ?? ModelNames.CreateIndexModelName(_key, i.ToString());
                    var metadata = entry?.Metadata ?? _metadata.ElementMetadata;

                    if ((entry != null && entry.SuppressValidation) || _modelState.HasReachedMaxErrors)
                    {
                        SuppressValidation(key);
                    }
                    else if (!Visit(key, metadata, model))
                    {
                        isValid = false;
                    }
                }
            }

            // Double-checking HasReachedMaxErrors just in case this model has no properties.
            if (isValid && !_modelState.HasReachedMaxErrors)
            {
                isValid &= ValidateNode();
            }

            return isValid;
        }

        private bool VisitComplexType()
        {
            var isValid = true;

            if (_model != null && ShouldValidateProperties(_metadata))
            {
                foreach (var property in _metadata.Properties)
                {
                    var model = property.PropertyGetter(_model);

                    ValidationState entry;
                    if (model == null)
                    {
                        entry = null;
                    }
                    else
                    {
                        _validationState.TryGetValue(model, out entry);
                    }

                    var key = entry?.Key ?? ModelNames.CreatePropertyModelName(_key, property.PropertyName);
                    var metadata = entry?.Metadata ?? property;

                    if ((entry != null && entry.SuppressValidation) || _modelState.HasReachedMaxErrors)
                    {
                        SuppressValidation(key);
                    }
                    else if (!Visit(key, metadata, model))
                    {
                        isValid = false;
                    }
                }
            }
            else if (_model != null)
            {
                SuppressValidation(_key);
            }

            // Double-checking HasReachedMaxErrors just in case this model has no properties.
            if (isValid && !_modelState.HasReachedMaxErrors)
            {
                isValid &= ValidateNode();
            }

            return isValid;
        }

        private bool VisitSimpleType()
        {
            if (_modelState.HasReachedMaxErrors)
            {
                SuppressValidation(_key);
                return false;
            }

            return ValidateNode();
        }

        private IList<IModelValidator> GetValidators(ModelMetadata metadata)
        {
            var context = new ModelValidatorProviderContext(metadata);
            _validatorProvider.GetValidators(context);
            return context.Validators;
        }

        private void SuppressValidation(string key)
        {
            var entries = _modelState.FindKeysWithPrefix(key);
            foreach (var entry in entries)
            {
                entry.Value.ValidationState = ModelValidationState.Skipped;
            }
        }

        private bool ShouldValidateProperties(ModelMetadata metadata)
        {
            var count = _excludeFilters.Count;
            for (var i = 0; i < _excludeFilters.Count; i++)
            {
                if (_excludeFilters[i].IsTypeExcluded(metadata.UnderlyingOrModelType))
                {
                    return false;
                }
            }

            return true;
        }

        private struct Recursifier : IDisposable
        {
            private readonly ValidationVisitor _visitor;
            private readonly object _container;
            private readonly string _key;
            private readonly ModelMetadata _metadata;
            private readonly object _model;
            private readonly object _newModel;

            public static Recursifier DoTheNeedful(ValidationVisitor visitor, string key, ModelMetadata metadata, object model)
            {
                var recursifier = new Recursifier(visitor, model);

                visitor._container = visitor._model;
                visitor._key = key;
                visitor._metadata = metadata;
                visitor._model = model;

                return recursifier;
            }

            public Recursifier(ValidationVisitor visitor, object newModel)
            {
                _visitor = visitor;
                _newModel = newModel;

                _container = _visitor._container;
                _key = _visitor._key;
                _metadata = _visitor._metadata;
                _model = _visitor._model;
            }

            public void Dispose()
            {
                _visitor._container = _container;
                _visitor._key = _key;
                _visitor._metadata = _metadata;
                _visitor._model = _model;

                _visitor._currentPath.Remove(_newModel);
            }
        }
    }
}
