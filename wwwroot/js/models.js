function openEditModel(
    id,
    modelId,
    endpoint,
    contextWindow,
    inputPrice,
    outputPrice,
    isActive,
    supportsVision,
    supportsStructuredOutput,
    apiKeyMask
) {
    document.getElementById('edit-id').value = id;
    document.getElementById('edit-model-id').value = modelId;
    document.getElementById('edit-endpoint').value = endpoint;
    // Only a masked preview of the key reaches the browser — never the full secret. Leave the
    // input blank on open; an empty field means "keep the existing key" (see UpdateAiModelUseCase).
    document.getElementById('edit-api-key').value = '';
    document.getElementById('edit-api-key-preview').textContent = apiKeyMask || '(chưa cấu hình)';
    document.getElementById('edit-context-window').value = contextWindow;
    document.getElementById('edit-input-price').value = inputPrice;
    document.getElementById('edit-output-price').value = outputPrice;

    document.getElementById('edit-is-active').checked = isActive === 'true';
    document.getElementById('edit-supports-vision').checked = supportsVision === 'true';
    document.getElementById('edit-supports-structured-output').checked = supportsStructuredOutput === 'true';

    openModal('editModel');
}
