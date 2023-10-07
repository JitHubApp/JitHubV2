﻿using Markdig.Extensions.TaskLists;
using Markdig.UWP.TextElements;
using System;

namespace Markdig.UWP.Renderers.ObjectRenderers.Extensions;

internal class TaskListRenderer : UWPObjectRenderer<TaskList>
{
    protected override void Write(UWPRenderer renderer, TaskList taskList)
    {
        if (renderer == null) throw new ArgumentNullException(nameof(renderer));
        if (taskList == null) throw new ArgumentNullException(nameof(taskList));

        var checkBox = new MyTaskListCheckBox(taskList);
        renderer.WriteInline(checkBox);
    }
}
