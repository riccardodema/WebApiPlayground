using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace WebApiPlayground.Api.Validation;

/// <summary>
/// Esegue i validator FluentValidation sugli argomenti dell'action (es. i body DTO) dopo il
/// model binding. Per ogni argomento risolve l'eventuale <c>IValidator&lt;T&gt;</c> dal container;
/// le violazioni confluiscono nel <see cref="Microsoft.AspNetCore.Mvc.ModelBinding.ModelStateDictionary"/>
/// e la risposta 400 è prodotta dallo <b>stesso</b> <c>InvalidModelStateResponseFactory</c> usato per
/// le DataAnnotations: un'unica forma d'errore ProblemDetails per entrambi i canali.
/// </summary>
/// <remarks>
/// È l'integrazione "manuale" raccomandata: il pacchetto <c>FluentValidation.AspNetCore</c>
/// (auto-binding) è deprecato. Il filtro è generico e si applica a ogni endpoint senza modifiche.
/// </remarks>
public sealed class ValidationFilter : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        foreach (var argument in context.ActionArguments.Values)
        {
            if (argument is null)
                continue;

            var validatorType = typeof(IValidator<>).MakeGenericType(argument.GetType());
            if (context.HttpContext.RequestServices.GetService(validatorType) is not IValidator validator)
                continue;

            var validationContext = new ValidationContext<object>(argument);
            var result = await validator.ValidateAsync(validationContext, context.HttpContext.RequestAborted);

            foreach (var error in result.Errors)
                context.ModelState.AddModelError(error.PropertyName, error.ErrorMessage);
        }

        if (!context.ModelState.IsValid)
        {
            var apiBehavior = context.HttpContext.RequestServices
                .GetRequiredService<IOptions<ApiBehaviorOptions>>().Value;
            context.Result = apiBehavior.InvalidModelStateResponseFactory(context);
            return;
        }

        await next();
    }
}
